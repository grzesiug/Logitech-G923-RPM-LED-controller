using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using GameReaderCommon;
using HidSharp;
using SimHub.Plugins;
using log4net;

namespace G923LedPlugin
{
    [PluginDescription("Logitech G923 RPM LED controller (PS/PC & Xbox/PC)")]
    [PluginAuthor("Grzegorz Ginalski")]
    [PluginName("G923 LED Plugin v1.3.2 Beta")]
    public class G923LedPlugin : IPlugin, IDataPlugin
    {
        private const string PluginName = "G923 LED Plugin v1.3.2 Beta";

        private const bool EnableDiagnostics = true;

        // ── USB identifiers ──────────────────────────────────────────
        private const ushort VID_LOGITECH = 0x046D;
        private static readonly ushort[] SUPPORTED_PIDS_PS = { 0xC266 };
        private static readonly ushort[] SUPPORTED_PIDS_XBOX = { 0xC26D, 0xC26E };

        // ── RPM thresholds ───────────────────────────────────────────
        private const double LED_START_PCT = 0.65;
        private const double LED_SHIFT_PCT = 0.95;

        // ── Reconnect control ────────────────────────────────────────
        private static readonly TimeSpan RECONNECT_COOLDOWN = TimeSpan.FromSeconds(3);
        private DateTime _lastReconnectAttempt = DateTime.MinValue;
        private volatile bool _isConnecting = false;

        private static readonly ILog Log = LogManager.GetLogger(typeof(G923LedPlugin));

        private readonly object _deviceLock = new object();
        private readonly object _xboxLock = new object();

        // ── PS/PC mode state ─────────────────────────────────────────
        private HidStream _psDevice;
        private byte _lastBitmask = 0xFF;

        // ── Xbox/PC mode state (HID++) ───────────────────────────────
        //
        // G923 Xbox/PC (C26D/C26E) eksponuje DWIE kolekcje HID++ (potwierdzone logiem):
        //   mi_00&col02  maxOut=20  UsagePage=0xFF43/0x0602  → obsługuje SHORT(0x10) i LONG(0x11)
        //   mi_00&col03  maxOut=64  UsagePage=0xFF43/0x0604  → odpowiedzi (VERY LONG 0x12)
        //
        // Kolekcja SHORT (maxOut=7) NIE ISTNIEJE na tym kole w przeciwieństwie do G PRO.
        // Raporty SHORT (0x10, 7B) są wysyłane przez col02 (maxOut=20) z dopełnieniem do 20B.
        // Raporty LONG  (0x11, 20B) też przez col02.
        // Odpowiedzi HID++ czytamy z col03 (64B).
        //
        // _xboxCmd  = col02 (20B) – zapis: getFeature, ARM, SendPair
        // _xboxReply = col03 (64B) – odczyt: odpowiedzi getFeature (może być null → fallback na _xboxCmd)
        private HidStream _xboxCmd;
        private HidStream _xboxReply;
        private int _xboxCmdMaxOut;   // maxOutputReportLength dla _xboxCmd (zazwyczaj 20)
        private byte _featureIndex;

        private int _targetLevel;
        private int _sentLevel = -1;
        private long _lastWriteMs;
        private Thread _senderThread;
        private volatile bool _senderRunning;
        private int _senderWriteErrors;
        private const int MAX_WRITE_ERRORS = 5;

        // ── Shared state ─────────────────────────────────────────────
        private bool _isXboxMode;
        private bool _isBlinking;
        private int _blinkCounter;

        // ── HID++ protocol constants ─────────────────────────────────
        private const byte REP_SHORT = 0x11;
        private const int LEN_SHORT = 7;
        private const byte REP_LONG = 0x11;
        private const int LEN_LONG = 20;
        private const int LEN_VERY_LONG = 64;
        private const byte DEV_WIRED = 0xFF;
        private const byte ROOT_INDEX = 0x00;
        private const byte ROOT_GET_FEATURE = 0x0B;
        private const ushort PAGE_REV_LIGHTS = 0x807A;
        private const byte SOFTWARE_ID = 0x0D;
        private const int ARM_GAP_MS = 4;
        private const int KEEPALIVE_MS = 5000;
        private const int CHANGE_MIN_MS = 160;

        public PluginManager PluginManager { get; set; }

        // ════════════════════════════════════════════════════════════
        //  IPlugin / IDataPlugin
        // ════════════════════════════════════════════════════════════

        public void Init(PluginManager pluginManager)
        {
            Log.Info($"[{PluginName}] Init – {(Environment.Is64BitProcess ? "x64" : "x86")}");
            TryConnect();
        }

        public void DataUpdate(PluginManager pluginManager, ref GameData data)
        {
            bool needsReconnect = false;
            lock (_deviceLock)
            {
                if (_isXboxMode && (_xboxCmd == null || !_xboxCmd.CanWrite)) needsReconnect = true;
                if (!_isXboxMode && (_psDevice == null || !_psDevice.CanWrite)) needsReconnect = true;
            }

            if (needsReconnect)
            {
                if (!_isConnecting && (DateTime.Now - _lastReconnectAttempt) > RECONNECT_COOLDOWN)
                {
                    _lastReconnectAttempt = DateTime.Now;
                    TryConnect();
                }
                return;
            }

            if (!data.GameRunning || data.NewData.MaxRpm <= 0)
            {
                TurnOffLeds();
                return;
            }

            double pct = data.NewData.Rpms / data.NewData.MaxRpm;

            lock (_deviceLock)
            {
                if (_isXboxMode)
                {
                    if (_xboxCmd == null) return;
                    if (pct >= LED_SHIFT_PCT)
                    {
                        _blinkCounter++;
                        if (_blinkCounter >= 5) { _blinkCounter = 0; _isBlinking = !_isBlinking; }
                        SetXboxLevelUnsafe(_isBlinking ? 10 : 0);
                    }
                    else
                    {
                        _blinkCounter = 0;
                        _isBlinking = false;
                        // Skaluj od LED_START_PCT do LED_SHIFT_PCT → 0..10 LEDów
                        int lvl = pct < LED_START_PCT ? 0
                            : (int)Math.Round((pct - LED_START_PCT) / (LED_SHIFT_PCT - LED_START_PCT) * 10);
                        SetXboxLevelUnsafe(Math.Max(0, Math.Min(10, lvl)));
                    }
                }
                else
                {
                    if (_psDevice == null) return;
                    if (pct >= LED_SHIFT_PCT)
                    {
                        _blinkCounter++;
                        if (_blinkCounter >= 5) { _blinkCounter = 0; _isBlinking = !_isBlinking; }
                        SendPsLedsUnsafe(_isBlinking ? (byte)0x1F : (byte)0x00);
                    }
                    else
                    {
                        _blinkCounter = 0;
                        _isBlinking = false;
                        SendPsLedsUnsafe(RpmToBitmask(pct));
                    }
                }
            }
        }

        public void End(PluginManager pluginManager)
        {
            Log.Info($"[{PluginName}] End – shutting down");
            StopSender();
            lock (_deviceLock) { ShutdownPsUnsafe(); ShutdownXboxUnsafe(); }
        }

        // ════════════════════════════════════════════════════════════
        //  Connection logic
        // ════════════════════════════════════════════════════════════

        private void TryConnect()
        {
            if (_isConnecting) return;
            _isConnecting = true;
            try
            {
                if (EnableDiagnostics) DumpAllLogitechDevices();

                lock (_deviceLock)
                {
                    ShutdownPsUnsafe();
                    ShutdownXboxUnsafe();
                    _blinkCounter = 0;
                    _isBlinking = false;
                }

                var psDevices = FindDevices(VID_LOGITECH, SUPPORTED_PIDS_PS);
                var xboxDevices = FindDevices(VID_LOGITECH, SUPPORTED_PIDS_XBOX);

                if (psDevices.Count > 0)
                {
                    var ps = TryInitPsMode(psDevices);
                    if (ps != null)
                    {
                        lock (_deviceLock) { _psDevice = ps; _lastBitmask = 0xFF; _isXboxMode = false; }
                        Log.Info($"[{PluginName}] PS/PC mode active");
                        return;
                    }
                    Log.Warn($"[{PluginName}] PS/PC init failed, trying Xbox/PC");
                }

                if (xboxDevices.Count > 0)
                {
                    if (TryInitXboxMode(xboxDevices))
                    {
                        _targetLevel = 0; _sentLevel = -1; _lastWriteMs = 0; _senderWriteErrors = 0;
                        StartSender();
                        Log.Info($"[{PluginName}] Xbox/PC mode active (feat=0x{_featureIndex:X2}, cmdMaxOut={_xboxCmdMaxOut})");
                        return;
                    }
                    Log.Error($"[{PluginName}] Xbox/PC init failed");
                }

                Log.Error($"[{PluginName}] No compatible G923 found");
            }
            catch (Exception ex) { Log.Error($"[{PluginName}] TryConnect: {ex.Message}"); }
            finally { _isConnecting = false; }
        }

        private List<HidDevice> FindDevices(ushort vid, ushort[] pids)
        {
            var result = new List<HidDevice>();
            var list = DeviceList.Local;
            foreach (var pid in pids) result.AddRange(list.GetHidDevices(vid, pid));
            return result;
        }

        // ════════════════════════════════════════════════════════════
        //  PS/PC
        // ════════════════════════════════════════════════════════════

        private HidStream TryInitPsMode(List<HidDevice> devices)
        {
            foreach (var dev in devices)
            {
                try
                {
                    var s = dev.Open();
                    if (s == null) continue;
                    s.ReadTimeout = s.WriteTimeout = 250;
                    s.Write(new byte[] { 0x00, 0xF8, 0x12, 0x00, 0x00, 0x00, 0x00, 0x01 });
                    return s;
                }
                catch (Exception ex) { Log.Error($"[{PluginName}] PS open: {ex.Message}"); }
            }
            return null;
        }

        private void SendPsLedsUnsafe(byte bitmask)
        {
            if (_psDevice == null || bitmask == _lastBitmask) return;
            try
            {
                _psDevice.Write(new byte[] { 0x00, 0xF8, 0x12, bitmask, 0x00, 0x00, 0x00, 0x01 });
                _lastBitmask = bitmask;
            }
            catch (Exception ex) { Log.Error($"[{PluginName}] PS write: {ex.Message}"); _psDevice = null; }
        }

        private static byte RpmToBitmask(double pct)
        {
            if (pct < LED_START_PCT) return 0x00;
            double progress = (pct - LED_START_PCT) / (LED_SHIFT_PCT - LED_START_PCT);
            int leds = Math.Max(0, Math.Min(5, (int)Math.Ceiling(progress * 5)));
            byte mask = 0;
            for (int i = 0; i < leds; i++) mask |= (byte)(1 << i);
            return mask;
        }

        private void ShutdownPsUnsafe()
        {
            if (_psDevice == null) return;
            try { _psDevice.Write(new byte[] { 0x00, 0xF8, 0x12, 0x00, 0x00, 0x00, 0x00, 0x01 }); } catch { }
            try { _psDevice.Dispose(); } catch { }
            _psDevice = null; _lastBitmask = 0xFF;
        }

        // ════════════════════════════════════════════════════════════
        //  Xbox/PC – inicjalizacja HID++
        // ════════════════════════════════════════════════════════════

        /// <summary>
        /// Grupujemy kolekcje wg pnia ścieżki (bez &amp;col*), pomijamy mi_01 (Trueforce),
        /// a potem dla każdej grupy szukamy:
        ///   - kolekcji CMD:   maxOut == 20 (col02 na G923 Xbox) – do zapisu
        ///   - kolekcji REPLY: maxOut == 64 (col03 na G923 Xbox) – do odczytu odpowiedzi
        ///
        /// Jeśli CMD ma maxOut=20, SHORT (7B) jest dopełniany do 20B przed zapisem.
        /// Takie dopełnienie jest WYMAGANE gdy nie ma osobnej kolekcji 7B.
        /// </summary>
        private bool TryInitXboxMode(List<HidDevice> devices)
        {
            var groups = new Dictionary<string, List<HidDevice>>(StringComparer.OrdinalIgnoreCase);
            foreach (var dev in devices)
            {
                string path = dev.DevicePath ?? string.Empty;
                // Pomijamy interfejs Trueforce (mi_01 na G923, mi_02 na G PRO)
                if (path.IndexOf("mi_01", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                if (path.IndexOf("mi_02", StringComparison.OrdinalIgnoreCase) >= 0) continue;

                string stem = GroupStem(path);
                if (!groups.TryGetValue(stem, out var g)) groups[stem] = g = new List<HidDevice>();
                g.Add(dev);
                Log.Info($"[G923LED] candidate: maxOut={SafeOutLen(dev)} path={path}");
            }

            foreach (var kv in groups)
                if (TryOpenGroup(kv.Key, kv.Value)) return true;

            return false;
        }

        private static string GroupStem(string path)
        {
            int i = path.IndexOf("&col", StringComparison.OrdinalIgnoreCase);
            return i > 0 ? path.Substring(0, i) : path;
        }

        private static int SafeOutLen(HidDevice d)
        {
            try { return d.GetMaxOutputReportLength(); } catch { return -1; }
        }

        private bool TryOpenGroup(string stem, List<HidDevice> group)
        {
            var opened = new List<HidStream>();
            try
            {
                // Szukamy:
                //   cmdDev   – kolekcja do zapisu:  preferuj maxOut=7, fallback na maxOut=20
                //   replyDev – kolekcja do odczytu: maxOut=64 (lub maxOut=20 jako fallback)
                HidDevice cmdDev = null, replyDev = null;
                int cmdMaxOut = 0;

                foreach (var dev in group)
                {
                    int outLen = SafeOutLen(dev);
                    if (outLen == LEN_SHORT && cmdDev == null)
                    {
                        cmdDev = dev; cmdMaxOut = LEN_SHORT;
                    }
                    else if (outLen == LEN_LONG && cmdDev == null)
                    {
                        // G923 Xbox/PC nie ma kolekcji 7B – col02 (20B) pełni rolę CMD
                        cmdDev = dev; cmdMaxOut = LEN_LONG;
                    }
                    else if (outLen == LEN_VERY_LONG && replyDev == null)
                    {
                        replyDev = dev;
                    }
                }

                if (cmdDev == null)
                {
                    Log.Warn($"[G923LED] brak kolekcji CMD (7B lub 20B): {stem}");
                    return false;
                }

                HidStream cmdS = null, replyS = null;
                try { cmdS = cmdDev.Open(new OpenConfiguration()); }
                catch (Exception ex) { Log.Warn($"[G923LED] CMD open failed: {ex.Message}"); return false; }
                cmdS.ReadTimeout = cmdS.WriteTimeout = 250;
                opened.Add(cmdS);

                if (replyDev != null)
                {
                    try
                    {
                        replyS = replyDev.Open(new OpenConfiguration());
                        replyS.ReadTimeout = replyS.WriteTimeout = 250;
                        opened.Add(replyS);
                        Log.Info($"[G923LED] Opened reply stream (64B): {replyDev.DevicePath}");
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"[G923LED] reply open failed (fallback do CMD): {ex.Message}");
                        replyS = null;
                    }
                }

                Log.Info($"[G923LED] CMD maxOut={cmdMaxOut}, reply={(replyS != null ? "64B" : "CMD fallback")}");

                // getFeature – żądanie SHORT wysyłamy przez CMD (dopełnione do cmdMaxOut)
                HidStream readFrom = replyS ?? cmdS;
                byte feat = TryGetFeature(cmdS, readFrom, cmdMaxOut);
                if (feat == 0)
                {
                    Log.Warn($"[G923LED] getFeature 0x807A brak odpowiedzi: {stem}");
                    DisposeAll(opened); return false;
                }

                // ARM
                if (!ArmLights(cmdS, feat, cmdMaxOut))
                {
                    Log.Error($"[G923LED] ARM failed: {stem}");
                    DisposeAll(opened); return false;
                }

                // Poziom 0 po ARM
                SendPairCore(cmdS, feat, 0, cmdMaxOut);

                lock (_deviceLock)
                {
                    _xboxCmd = cmdS;
                    _xboxReply = replyS;
                    _xboxCmdMaxOut = cmdMaxOut;
                    _featureIndex = feat;
                    _isXboxMode = true;
                }

                Log.Info($"[G923LED] OK: feat=0x{feat:X2} cmdMaxOut={cmdMaxOut}");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error($"[G923LED] TryOpenGroup error ({stem}): {ex.Message}");
                DisposeAll(opened);
                return false;
            }
        }

        private static void DisposeAll(List<HidStream> streams)
        {
            foreach (var s in streams) { try { s.Dispose(); } catch { } }
        }

        // ════════════════════════════════════════════════════════════
        //  HID++ getFeature
        // ════════════════════════════════════════════════════════════

        /// <summary>
        /// Wysyłamy SHORT (7B) przez cmdStream, dopełniony do cmdMaxOut.
        /// Dopełnienie jest KONIECZNE gdy cmdMaxOut=20 (G923 Xbox bez kolekcji 7B).
        /// Odpowiedź czytamy z readStream (col03 lub fallback col02).
        /// </summary>
        private byte TryGetFeature(HidStream cmdStream, HidStream readStream, int cmdMaxOut)
        {
            var req = new byte[LEN_SHORT];
            req[0] = REP_SHORT; req[1] = DEV_WIRED; req[2] = ROOT_INDEX;
            req[3] = ROOT_GET_FEATURE;
            req[4] = (byte)(PAGE_REV_LIGHTS >> 8);
            req[5] = (byte)(PAGE_REV_LIGHTS & 0xFF);
            req[6] = 0x00;

            byte[] paddedReq = PadToLength(req, cmdMaxOut);
            if (EnableDiagnostics)
                Log.Info($"[G923LED] getFeature → {BitConverter.ToString(paddedReq)}");

            try { cmdStream.Write(paddedReq); }
            catch (Exception ex) { Log.Error($"[G923LED] getFeature write: {ex.Message}"); return 0; }

            // Czytaj odpowiedź z readStream, jeśli różny od cmd – też spróbuj cmd
            var streams = readStream != cmdStream
                ? new[] { readStream, cmdStream }
                : new[] { cmdStream };

            foreach (var s in streams)
            {
                byte idx = ReadFeatureReply(s);
                if (idx == 0xFF) return 0;
                if (idx != 0) return idx;
            }
            return 0;
        }

        private byte ReadFeatureReply(HidStream s)
        {
            if (s == null) return 0;
            var buf = new byte[LEN_VERY_LONG];
            for (int attempt = 0; attempt < 4; attempt++)
            {
                int n;
                try { n = s.Read(buf, 0, buf.Length); }
                catch (TimeoutException) { return 0; }
                catch (Exception ex) { Log.Error($"[G923LED] getFeature read: {ex.Message}"); return 0; }

                if (n < 5) continue;
                if (buf[1] != DEV_WIRED || buf[2] != ROOT_INDEX) continue;
                if (buf[3] == 0xFF) { Log.Error("[G923LED] getFeature: HID++ error 0xFF"); return 0xFF; }
                byte idx = buf[4];
                if (idx != 0 && idx < 0x80)
                {
                    Log.Info($"[G923LED] getFeature OK: idx=0x{idx:X2}");
                    return idx;
                }
            }
            return 0;
        }

        // ════════════════════════════════════════════════════════════
        //  HID++ ARM
        // ════════════════════════════════════════════════════════════

        private bool ArmLights(HidStream cmdS, byte feat, int cmdMaxOut)
        {
            bool SendFn(int fn, byte p1 = 0)
            {
                var cmd = new byte[LEN_SHORT] { REP_SHORT, DEV_WIRED, feat, FnByte(fn), p1, 0, 0 };
                var padded = PadToLength(cmd, cmdMaxOut);
                try
                {
                    cmdS.Write(padded);
                    if (EnableDiagnostics) Log.Info($"[G923LED] ARM fn{fn} → {BitConverter.ToString(padded)}");
                    return true;
                }
                catch (Exception ex) { Log.Error($"[G923LED] ARM fn{fn}: {ex.Message}"); return false; }
            }

            if (!SendFn(0)) return false; ArmGap();
            if (!SendFn(1)) return false; ArmGap();
            if (!SendFn(2)) return false; ArmGap();
            if (!SendFn(3, 0x02)) return false; ArmGap();
            if (!SendFn(0)) return false; ArmGap();
            return true;
        }

        private static void ArmGap() { try { Thread.Sleep(ARM_GAP_MS); } catch { } }
        private static byte FnByte(int fn) => (byte)((fn << 4) | SOFTWARE_ID);

        // ════════════════════════════════════════════════════════════
        //  Para aktualizacji LED
        // ════════════════════════════════════════════════════════════

        /// <summary>
        /// SHORT (fn2) i LONG (fn6) idą przez ten sam strumień CMD.
        /// SHORT jest dopełniany do cmdMaxOut (20B na G923 Xbox).
        /// LONG ma stałe 20B – pasuje do col02 bezpośrednio.
        /// </summary>
        private void SendPairCore(HidStream cmdS, byte feat, int level, int cmdMaxOut)
        {
            byte lvl = (byte)Math.Max(0, Math.Min(10, level));

            // SHORT fn2 – dopełniamy do cmdMaxOut
            var shortCmd = new byte[LEN_SHORT] { REP_SHORT, DEV_WIRED, feat, FnByte(2), 0, 0, 0 };
            cmdS.Write(PadToLength(shortCmd, cmdMaxOut));

            // LONG fn6 – stałe 20B, bajt 9 = poziom LED
            var longCmd = new byte[LEN_LONG];
            longCmd[0] = REP_LONG; longCmd[1] = DEV_WIRED; longCmd[2] = feat; longCmd[3] = FnByte(6);
            longCmd[4] = 0x00; longCmd[5] = 0x01; longCmd[6] = 0x00; longCmd[7] = 0x0A; longCmd[8] = 0x00;
            longCmd[9] = lvl;
            cmdS.Write(longCmd);

            Interlocked.Exchange(ref _lastWriteMs, DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond);
            if (EnableDiagnostics) Log.Info($"[G923LED] SendPair level={lvl}");
        }

        // ════════════════════════════════════════════════════════════
        //  SenderLoop
        // ════════════════════════════════════════════════════════════

        private void StartSender()
        {
            _senderRunning = true;
            _senderThread = new Thread(SenderLoop) { IsBackground = true, Name = "G923LedSender" };
            _senderThread.Start();
        }

        private void StopSender()
        {
            _senderRunning = false;
            try { _senderThread?.Join(500); } catch { }
            _senderThread = null;
        }

        private void SenderLoop()
        {
            while (_senderRunning)
            {
                Thread.Sleep(30);
                if (!_senderRunning) break;

                long now = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
                long lastWrite = Interlocked.Read(ref _lastWriteMs);
                int target, sent;
                lock (_xboxLock) { target = _targetLevel; sent = _sentLevel; }

                bool changed = target != sent;
                bool dueChange = changed && (now - lastWrite) >= CHANGE_MIN_MS;
                bool dueKeep = !changed && (now - lastWrite) >= KEEPALIVE_MS;
                if (!dueChange && !dueKeep) continue;

                HidStream cmdS;
                byte feat;
                int maxOut;
                lock (_deviceLock) { cmdS = _xboxCmd; feat = _featureIndex; maxOut = _xboxCmdMaxOut; }
                if (cmdS == null) continue;

                int finalTarget;
                lock (_xboxLock) { finalTarget = _targetLevel; }

                try
                {
                    SendPairCore(cmdS, feat, finalTarget, maxOut);
                    lock (_xboxLock) { _sentLevel = finalTarget; }
                    _senderWriteErrors = 0; // reset on success
                }
                catch (Exception ex)
                {
                    _senderWriteErrors++;
                    Log.Warn($"[{PluginName}] SenderLoop write error #{_senderWriteErrors}: {ex.Message}");
                    if (_senderWriteErrors >= MAX_WRITE_ERRORS)
                    {
                        Log.Error($"[{PluginName}] Too many write errors – marking device lost");
                        lock (_deviceLock) { _xboxCmd = null; }
                        _senderWriteErrors = 0;
                    }
                }
            }
        }

        private void SetXboxLevelUnsafe(int level)
        {
            level = Math.Max(0, Math.Min(10, level));
            lock (_xboxLock) { _targetLevel = level; }
        }

        // ════════════════════════════════════════════════════════════
        //  Shutdown / TurnOff
        // ════════════════════════════════════════════════════════════

        private void ShutdownXboxUnsafe()
        {
            StopSender();
            if (_xboxCmd != null)
                try { SendPairCore(_xboxCmd, _featureIndex, 0, _xboxCmdMaxOut); } catch { }
            try { _xboxCmd?.Dispose(); } catch { }
            try { _xboxReply?.Dispose(); } catch { }
            _xboxCmd = _xboxReply = null;
        }

        private void TurnOffLeds()
        {
            lock (_deviceLock)
            {
                if (_isXboxMode)
                {
                    // Don't call SendPairCore directly here — that triggers an immediate HID++ packet
                    // on every BeamNG reconnect (which happens multiple times per session) and can
                    // cause the wheel to reset its internal HID++ state, disrupting FFB.
                    // Instead, just update the target level; SenderLoop will send it on its next tick.
                    SetXboxLevelUnsafe(0);
                    return;
                }
                else
                {
                    if (_psDevice != null)
                        try
                        {
                            _psDevice.Write(new byte[] { 0x00, 0xF8, 0x12, 0x00, 0x00, 0x00, 0x00, 0x01 });
                            _lastBitmask = 0x00;
                        }
                        catch { }
                }
            }
        }

        // ════════════════════════════════════════════════════════════
        //  Helpers
        // ════════════════════════════════════════════════════════════

        private static byte[] PadToLength(byte[] source, int targetLength)
        {
            if (source.Length >= targetLength) return source;
            var padded = new byte[targetLength];
            Array.Copy(source, padded, source.Length);
            return padded;
        }

        // ════════════════════════════════════════════════════════════
        //  Diagnostics
        // ════════════════════════════════════════════════════════════

        private void DumpAllLogitechDevices()
        {
            Log.Info("[G923LED DIAG] ===== START =====");
            int idx = 0;
            foreach (var dev in DeviceList.Local.GetHidDevices(VID_LOGITECH))
            {
                idx++;
                try
                {
                    int maxOut = -1, maxIn = -1;
                    try { maxOut = dev.GetMaxOutputReportLength(); } catch { }
                    try { maxIn = dev.GetMaxInputReportLength(); } catch { }
                    Log.Info($"[G923LED DIAG] #{idx} VID/PID=0x{dev.VendorID:X4}/0x{dev.ProductID:X4} maxOut={maxOut} maxIn={maxIn} path={dev.DevicePath}");
                }
                catch (Exception ex) { Log.Error($"[G923LED DIAG] #{idx} error: {ex.Message}"); }
            }
            Log.Info("[G923LED DIAG] ===== END =====");
        }
    }
}