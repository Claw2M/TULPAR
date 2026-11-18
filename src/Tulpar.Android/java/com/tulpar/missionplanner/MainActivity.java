package com.tulpar.missionplanner;

import android.annotation.SuppressLint;
import android.app.Activity;
import android.graphics.Color;
import android.os.Bundle;
import android.view.View;
import android.view.Window;
import android.view.WindowManager;
import android.webkit.JavascriptInterface;
import android.webkit.WebChromeClient;
import android.webkit.WebSettings;
import android.webkit.WebView;
import android.webkit.WebViewClient;

import org.json.JSONArray;
import org.json.JSONObject;

import java.io.InputStream;
import java.io.OutputStream;
import java.net.DatagramPacket;
import java.net.DatagramSocket;
import java.net.InetAddress;
import java.net.InetSocketAddress;
import java.net.Socket;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.nio.charset.StandardCharsets;
import java.util.Locale;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.concurrent.atomic.AtomicInteger;

public class MainActivity extends Activity {
    WebView webView;
    final AtomicBoolean connected = new AtomicBoolean(false);
    final AtomicInteger txSeq = new AtomicInteger(0);
    volatile Socket tcpSocket;
    volatile OutputStream tcpOut;
    volatile DatagramSocket udpSocket;
    volatile InetAddress udpPeerAddr;
    volatile int udpPeerPort = 0;
    volatile Thread ioThread;
    volatile Thread hbThread;

    // Telemetry state
    volatile int targetSys = 1;
    volatile int targetComp = 1;
    volatile boolean isArmed = false;
    volatile int customMode = 0;
    volatile String modeName = "STABILIZE";
    volatile double lat = 39.933400;
    volatile double lon = 32.859700;
    volatile double altRel = 0.0;
    volatile double altMsl = 850.0;
    volatile double groundSpeed = 0.0;
    volatile double climbRate = 0.0;
    volatile double rollDeg = 0.0;
    volatile double pitchDeg = 0.0;
    volatile double yawDeg = 0.0;
    volatile double batteryVolts = 12.6;
    volatile int batteryPct = 100;
    volatile int gpsFix = 6;
    volatile int satellites = 10;
    volatile int activeWpSeq = 0;
    volatile double homeLat = 39.933400;
    volatile double homeLon = 32.859700;
    volatile boolean hasHome = false;
    volatile long lastRxTimeMs = 0;

    @SuppressLint({"SetJavaScriptEnabled", "AddJavascriptInterface"})
    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        requestWindowFeature(Window.FEATURE_NO_TITLE);
        getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
        applyImmersiveFullscreen();

        webView = new WebView(this);
        webView.setBackgroundColor(Color.BLACK);

        WebSettings settings = webView.getSettings();
        settings.setJavaScriptEnabled(true);
        settings.setDomStorageEnabled(true);
        settings.setDatabaseEnabled(true);
        settings.setAllowFileAccess(true);
        settings.setAllowContentAccess(true);
        settings.setAllowFileAccessFromFileURLs(true);
        settings.setAllowUniversalAccessFromFileURLs(true);
        settings.setMediaPlaybackRequiresUserGesture(false);
        settings.setMixedContentMode(WebSettings.MIXED_CONTENT_ALWAYS_ALLOW);
        settings.setCacheMode(WebSettings.LOAD_DEFAULT);

        webView.setWebViewClient(new WebViewClient());
        webView.setWebChromeClient(new WebChromeClient());
        webView.addJavascriptInterface(this, "TulparNative");

        setContentView(webView);
        webView.loadUrl("file:///android_asset/index.html");
    }

    @Override
    public void onWindowFocusChanged(boolean hasFocus) {
        super.onWindowFocusChanged(hasFocus);
        if (hasFocus) {
            applyImmersiveFullscreen();
        }
    }

    private void applyImmersiveFullscreen() {
        View decor = getWindow().getDecorView();
        decor.setSystemUiVisibility(
                View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY
                        | View.SYSTEM_UI_FLAG_LAYOUT_STABLE
                        | View.SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION
                        | View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN
                        | View.SYSTEM_UI_FLAG_HIDE_NAVIGATION
                        | View.SYSTEM_UI_FLAG_FULLSCREEN);
    }

    @Override
    protected void onDestroy() {
        closeConnection();
        if (webView != null) {
            webView.destroy();
        }
        super.onDestroy();
    }

    void closeConnection() {
        connected.set(false);
        try {
            if (tcpSocket != null) tcpSocket.close();
        } catch (Exception ignored) {
        }
        try {
            if (udpSocket != null) udpSocket.close();
        } catch (Exception ignored) {
        }
        tcpSocket = null;
        tcpOut = null;
        udpSocket = null;
    }

    void emitLog(final String severity, final String tag, final String text) {
        runOnUiThread(() -> {
            if (webView == null) return;
            try {
                JSONObject obj = new JSONObject();
                obj.put("severity", severity);
                obj.put("tag", tag);
                obj.put("text", text);
                webView.evaluateJavascript("window.onNativeLog && window.onNativeLog(" + obj.toString() + ");", null);
            } catch (Exception ignored) {
            }
        });
    }

    void pushTelemetryToUi() {
        runOnUiThread(() -> {
            if (webView == null) return;
            try {
                JSONObject t = new JSONObject();
                t.put("connected", connected.get());
                t.put("sysId", targetSys);
                t.put("armed", isArmed);
                t.put("customMode", customMode);
                t.put("modeName", modeName);
                t.put("lat", lat);
                t.put("lon", lon);
                t.put("altRel", altRel);
                t.put("altMsl", altMsl);
                t.put("groundSpeed", groundSpeed);
                t.put("climbRate", climbRate);
                t.put("rollDeg", rollDeg);
                t.put("pitchDeg", pitchDeg);
                t.put("yawDeg", yawDeg);
                t.put("batteryVolts", batteryVolts);
                t.put("batteryPct", batteryPct);
                t.put("gpsFix", gpsFix);
                t.put("satellites", satellites);
                t.put("activeWpSeq", activeWpSeq);
                t.put("homeLat", homeLat);
                t.put("homeLon", homeLon);
                t.put("hasHome", hasHome);
                double ageSec = lastRxTimeMs > 0 ? (System.currentTimeMillis() - lastRxTimeMs) / 1000.0 : -1.0;
                t.put("ageSec", ageSec);
                webView.evaluateJavascript("window.onNativeTelemetry && window.onNativeTelemetry(" + t.toString() + ");", null);
            } catch (Exception ignored) {
            }
        });
    }

    private static String resolveCopterMode(int cm) {
        switch (cm) {
            case 0: return "STABILIZE";
            case 1: return "ACRO";
            case 2: return "ALT_HOLD";
            case 3: return "AUTO";
            case 4: return "GUIDED";
            case 5: return "LOITER";
            case 6: return "RTL";
            case 7: return "CIRCLE";
            case 9: return "LAND";
            case 16: return "POSHOLD";
            case 17: return "BRAKE";
            case 21: return "SMART_RTL";
            default: return "MODE " + cm;
        }
    }

    private static int crcExtraForMsg(int msgId) {
        switch (msgId) {
            case 0: return 50;    // HEARTBEAT
            case 1: return 124;   // SYS_STATUS
            case 11: return 89;   // SET_MODE
            case 24: return 24;   // GPS_RAW_INT
            case 30: return 39;   // ATTITUDE
            case 33: return 104;  // GLOBAL_POSITION_INT
            case 39: return 254;  // MISSION_ITEM
            case 42: return 28;   // MISSION_CURRENT
            case 44: return 221;  // MISSION_COUNT
            case 45: return 232;  // MISSION_CLEAR_ALL
            case 66: return 148;  // REQUEST_DATA_STREAM
            case 73: return 38;   // MISSION_ITEM_INT
            case 74: return 20;   // VFR_HUD
            case 76: return 152;  // COMMAND_LONG
            case 77: return 143;  // COMMAND_ACK
            case 84: return 143;  // SET_POSITION_TARGET_LOCAL_NED
            case 86: return 5;    // SET_POSITION_TARGET_GLOBAL_INT
            case 253: return 83;  // STATUSTEXT
            default: return -1;
        }
    }

    private static int crcAccumulate(int b, int crc) {
        int tmp = (b ^ (crc & 0xFF)) & 0xFF;
        tmp = (tmp ^ ((tmp << 4) & 0xFF)) & 0xFF;
        return ((crc >> 8) ^ (tmp << 8) ^ (tmp << 3) ^ (tmp >> 4)) & 0xFFFF;
    }

    byte[] encodeMavlinkV2(int msgId, byte[] payload, int crcExtra) {
        int len = payload.length;
        byte[] frame = new byte[10 + len + 2];
        frame[0] = (byte) 0xFD;
        frame[1] = (byte) len;
        frame[2] = 0;
        frame[3] = 0;
        frame[4] = (byte) (txSeq.getAndIncrement() & 0xFF);
        frame[5] = (byte) 255; // GCS sysid
        frame[6] = (byte) 190; // GCS compid
        frame[7] = (byte) (msgId & 0xFF);
        frame[8] = (byte) ((msgId >> 8) & 0xFF);
        frame[9] = (byte) ((msgId >> 16) & 0xFF);
        System.arraycopy(payload, 0, frame, 10, len);

        int crc = 0xFFFF;
        for (int i = 1; i < 10 + len; i++) {
            crc = crcAccumulate(frame[i] & 0xFF, crc);
        }
        crc = crcAccumulate(crcExtra & 0xFF, crc);
        frame[10 + len] = (byte) (crc & 0xFF);
        frame[11 + len] = (byte) ((crc >> 8) & 0xFF);
        return frame;
    }

    synchronized void sendRawFrame(byte[] frame) {
        try {
            OutputStream out = tcpOut;
            if (out != null) {
                out.write(frame);
                out.flush();
                return;
            }
            DatagramSocket ds = udpSocket;
            InetAddress addr = udpPeerAddr;
            int port = udpPeerPort;
            if (ds != null && addr != null && port > 0) {
                DatagramPacket pkt = new DatagramPacket(frame, frame.length, addr, port);
                ds.send(pkt);
            }
        } catch (Exception ignored) {
        }
    }

    void sendGcsHeartbeat() {
        ByteBuffer bb = ByteBuffer.allocate(9).order(ByteOrder.LITTLE_ENDIAN);
        bb.putInt(0);
        bb.put((byte) 6); // MAV_TYPE_GCS
        bb.put((byte) 8); // MAV_AUTOPILOT_INVALID
        bb.put((byte) 0);
        bb.put((byte) 4); // MAV_STATE_ACTIVE
        bb.put((byte) 3);
        sendRawFrame(encodeMavlinkV2(0, bb.array(), 50));
    }

    void sendRequestDataStreams() {
        ByteBuffer bb = ByteBuffer.allocate(6).order(ByteOrder.LITTLE_ENDIAN);
        bb.putShort((short) 4); // 4 Hz
        bb.put((byte) targetSys);
        bb.put((byte) targetComp);
        bb.put((byte) 0); // MAV_DATA_STREAM_ALL
        bb.put((byte) 1); // start
        sendRawFrame(encodeMavlinkV2(66, bb.array(), 148));
    }

    void sendSetMode(int newCustomMode) {
        ByteBuffer bb = ByteBuffer.allocate(6).order(ByteOrder.LITTLE_ENDIAN);
        bb.putInt(newCustomMode);
        bb.put((byte) targetSys);
        bb.put((byte) 1); // MAV_MODE_FLAG_CUSTOM_MODE_ENABLED
        sendRawFrame(encodeMavlinkV2(11, bb.array(), 89));
    }

    void sendCommandLong(int command, float p1, float p2, float p3, float p4, float p5, float p6, float p7) {
        ByteBuffer bb = ByteBuffer.allocate(33).order(ByteOrder.LITTLE_ENDIAN);
        bb.putFloat(p1);
        bb.putFloat(p2);
        bb.putFloat(p3);
        bb.putFloat(p4);
        bb.putFloat(p5);
        bb.putFloat(p6);
        bb.putFloat(p7);
        bb.putShort((short) command);
        bb.put((byte) targetSys);
        bb.put((byte) targetComp);
        bb.put((byte) 0);
        sendRawFrame(encodeMavlinkV2(76, bb.array(), 152));
    }

    void sendGuidedPositionTarget(double targetLat, double targetLon, float targetAlt) {
        ByteBuffer bb = ByteBuffer.allocate(53).order(ByteOrder.LITTLE_ENDIAN);
        bb.putInt(0); // time_boot_ms
        bb.putInt((int) Math.round(targetLat * 1e7));
        bb.putInt((int) Math.round(targetLon * 1e7));
        bb.putFloat(targetAlt);
        bb.putFloat(0f); bb.putFloat(0f); bb.putFloat(0f);
        bb.putFloat(0f); bb.putFloat(0f); bb.putFloat(0f);
        bb.putFloat(0f); bb.putFloat(0f);
        bb.putShort((short) 0x0DF8); // position-only mask
        bb.put((byte) targetSys);
        bb.put((byte) targetComp);
        bb.put((byte) 6); // MAV_FRAME_GLOBAL_RELATIVE_ALT_INT
        sendRawFrame(encodeMavlinkV2(86, bb.array(), 5));
    }

    private void handleMavlinkMessage(int sysId, int compId, int msgId, byte[] payload) {
        lastRxTimeMs = System.currentTimeMillis();
        ByteBuffer bb = ByteBuffer.wrap(payload).order(ByteOrder.LITTLE_ENDIAN);
        switch (msgId) {
            case 0: // HEARTBEAT
                if (payload.length >= 9) {
                    int cm = bb.getInt(0);
                    int type = payload[4] & 0xFF;
                    if (type == 6) return; // ignore GCS heartbeat
                    targetSys = sysId;
                    targetComp = compId == 0 ? 1 : compId;
                    int baseMode = payload[6] & 0xFF;
                    customMode = cm;
                    isArmed = (baseMode & 0x80) != 0;
                    modeName = resolveCopterMode(cm);
                }
                break;
            case 1: // SYS_STATUS
                if (payload.length >= 31) {
                    int mv = bb.getShort(14) & 0xFFFF;
                    if (mv > 0 && mv != 65535) {
                        batteryVolts = mv / 1000.0;
                    }
                    int rem = payload[30];
                    if (rem >= 0 && rem <= 100) {
                        batteryPct = rem;
                    }
                }
                break;
            case 24: // GPS_RAW_INT
                if (payload.length >= 30) {
                    gpsFix = payload[28] & 0xFF;
                    satellites = payload[29] & 0xFF;
                }
                break;
            case 30: // ATTITUDE
                if (payload.length >= 28) {
                    rollDeg = Math.toDegrees(bb.getFloat(4));
                    pitchDeg = Math.toDegrees(bb.getFloat(8));
                    double y = Math.toDegrees(bb.getFloat(12));
                    if (y < 0) y += 360.0;
                    yawDeg = y;
                }
                break;
            case 33: // GLOBAL_POSITION_INT
                if (payload.length >= 28) {
                    int latInt = bb.getInt(4);
                    int lonInt = bb.getInt(8);
                    int altMslMm = bb.getInt(12);
                    int altRelMm = bb.getInt(16);
                    if (latInt != 0 || lonInt != 0) {
                        lat = latInt / 1e7;
                        lon = lonInt / 1e7;
                        if (!hasHome) {
                            homeLat = lat;
                            homeLon = lon;
                            hasHome = true;
                        }
                    }
                    altMsl = altMslMm / 1000.0;
                    altRel = altRelMm / 1000.0;
                }
                break;
            case 42: // MISSION_CURRENT
                if (payload.length >= 2) {
                    activeWpSeq = bb.getShort(0) & 0xFFFF;
                }
                break;
            case 74: // VFR_HUD
                if (payload.length >= 20) {
                    groundSpeed = bb.getFloat(4);
                    climbRate = bb.getFloat(16);
                }
                break;
            case 77: // COMMAND_ACK
                if (payload.length >= 3) {
                    int cmd = bb.getShort(0) & 0xFFFF;
                    int res = payload[2] & 0xFF;
                    String resStr = (res == 0) ? "Accepted" : ("Result=" + res);
                    emitLog(res == 0 ? "INFO" : "WARNING", "ACK", "Cmd #" + cmd + " -> " + resStr);
                }
                break;
            case 253: // STATUSTEXT
                if (payload.length >= 2) {
                    int sev = payload[0] & 0xFF;
                    int end = 1;
                    while (end < payload.length && end < 51 && payload[end] != 0) end++;
                    String txt = new String(payload, 1, end - 1, StandardCharsets.UTF_8).trim();
                    if (!txt.isEmpty()) {
                        emitLog(sev <= 3 ? "WARNING" : "INFO", "OTOPİLOT", txt);
                    }
                }
                break;
        }
    }

    int rxState = 0;
    boolean rxV2 = true;
    int rxLen = 0;
    int rxSysId = 1;
    int rxCompId = 1;
    int rxMsgId = 0;
    int rxIdx = 0;
    final byte[] rxPayload = new byte[280];

    void resetRxParser() {
        rxState = 0;
        rxIdx = 0;
    }

    void parseStreamBytes(byte[] buf, int count) {
        for (int i = 0; i < count; i++) {
            int b = buf[i] & 0xFF;
            switch (rxState) {
                case 0:
                    if (b == 0xFD) { rxV2 = true; rxState = 1; }
                    else if (b == 0xFE) { rxV2 = false; rxState = 1; }
                    break;
                case 1:
                    rxLen = b;
                    rxIdx = 0;
                    rxState = rxV2 ? 2 : 4;
                    break;
                case 2:
                    rxState = 3;
                    break;
                case 3:
                    rxState = 4;
                    break;
                case 4:
                    rxState = 5;
                    break;
                case 5:
                    rxSysId = b;
                    rxState = 6;
                    break;
                case 6:
                    rxCompId = b;
                    rxState = 7;
                    break;
                case 7:
                    rxMsgId = b;
                    rxState = rxV2 ? 8 : (rxLen > 0 ? 10 : 11);
                    break;
                case 8:
                    rxMsgId |= (b << 8);
                    rxState = 9;
                    break;
                case 9:
                    rxMsgId |= (b << 16);
                    rxState = rxLen > 0 ? 10 : 11;
                    break;
                case 10:
                    if (rxIdx < rxPayload.length) {
                        rxPayload[rxIdx] = (byte) b;
                    }
                    rxIdx++;
                    if (rxIdx >= rxLen) rxState = 11;
                    break;
                case 11:
                    rxState = 12;
                    break;
                case 12:
                    byte[] copy = new byte[64];
                    System.arraycopy(rxPayload, 0, copy, 0, Math.min(rxLen, copy.length));
                    handleMavlinkMessage(rxSysId, rxCompId, rxMsgId, copy);
                    rxState = 0;
                    break;
            }
        }
    }

        @JavascriptInterface
        public void connectTcp(final String host, final int port) {
            closeConnection();
            connected.set(true);
            ioThread = new Thread(() -> {
                try {
                    Socket s = new Socket();
                    s.connect(new InetSocketAddress(host, port), 4000);
                    tcpSocket = s;
                    tcpOut = s.getOutputStream();
                    emitLog("NOTICE", "BAĞLANTI", "Bağlandı: TCP " + host + ":" + port);
                    sendGcsHeartbeat();
                    sendRequestDataStreams();
                    startHeartbeatLoop();

                    InputStream in = s.getInputStream();
                    byte[] buf = new byte[2048];
                    resetRxParser();
                    while (connected.get()) {
                        int n = in.read(buf);
                        if (n <= 0) break;
                        parseStreamBytes(buf, n);
                    }
                } catch (Exception ex) {
                    emitLog("WARNING", "BAĞLANTI", "TCP bağlantı hatası: " + ex.getMessage());
                } finally {
                    closeConnection();
                    pushTelemetryToUi();
                }
            }, "Tulpar-TCP");
            ioThread.setDaemon(true);
            ioThread.start();
        }

        @JavascriptInterface
        public void connectUdp(final int localPort) {
            closeConnection();
            connected.set(true);
            ioThread = new Thread(() -> {
                try {
                    DatagramSocket ds = new DatagramSocket(localPort);
                    udpSocket = ds;
                    emitLog("NOTICE", "BAĞLANTI", "UDP dinleniyor: 0.0.0.0:" + localPort);
                    startHeartbeatLoop();

                    byte[] buf = new byte[2048];
                    DatagramPacket pkt = new DatagramPacket(buf, buf.length);
                    resetRxParser();
                    while (connected.get()) {
                        ds.receive(pkt);
                        udpPeerAddr = pkt.getAddress();
                        udpPeerPort = pkt.getPort();
                        parseStreamBytes(pkt.getData(), pkt.getLength());
                    }
                } catch (Exception ex) {
                    emitLog("WARNING", "BAĞLANTI", "UDP bağlantı kapandı: " + ex.getMessage());
                } finally {
                    closeConnection();
                    pushTelemetryToUi();
                }
            }, "Tulpar-UDP");
            ioThread.setDaemon(true);
            ioThread.start();
        }

        @JavascriptInterface
        public void disconnect() {
            closeConnection();
            emitLog("INFO", "BAĞLANTI", "Bağlantı kesildi.");
            pushTelemetryToUi();
        }

        @JavascriptInterface
        public void sendSafetyCommand(final String kind, final double paramA, final double paramB, final double paramC) {
            new Thread(() -> {
                try {
                    switch (kind) {
                        case "Arm":
                            sendCommandLong(400, 1f, 0f, 0f, 0f, 0f, 0f, 0f);
                            break;
                        case "Disarm":
                            sendCommandLong(400, 0f, 0f, 0f, 0f, 0f, 0f, 0f);
                            break;
                        case "Takeoff":
                            if (!"GUIDED".equalsIgnoreCase(modeName)) {
                                sendSetMode(4); // GUIDED
                                Thread.sleep(150);
                            }
                            sendCommandLong(22, 0f, 0f, 0f, 0f, 0f, 0f, (float) paramA);
                            break;
                        case "StartMission":
                            if (!"AUTO".equalsIgnoreCase(modeName)) {
                                sendSetMode(3); // AUTO
                                Thread.sleep(150);
                            }
                            sendCommandLong(300, 0f, 0f, 0f, 0f, 0f, 0f, 0f);
                            break;
                        case "Hold":
                            sendSetMode(5); // LOITER
                            break;
                        case "Rtl":
                            sendCommandLong(20, 0f, 0f, 0f, 0f, 0f, 0f, 0f);
                            break;
                        case "Land":
                            sendCommandLong(21, 0f, 0f, 0f, 0f, 0f, 0f, 0f);
                            break;
                        case "GuidedFlyHere":
                            if (!"GUIDED".equalsIgnoreCase(modeName)) {
                                sendSetMode(4); // GUIDED
                                Thread.sleep(120);
                            }
                            sendGuidedPositionTarget(paramA, paramB, (float) paramC);
                            break;
                    }
                    emitLog("NOTICE", "KOMUT", "Onaylı komut gönderildi: " + kind);
                } catch (Exception ex) {
                    emitLog("WARNING", "KOMUT", "Komut hatası: " + ex.getMessage());
                }
            }).start();
        }

        @JavascriptInterface
        public void setFlightMode(final int cm, final String name) {
            new Thread(() -> {
                sendSetMode(cm);
                emitLog("INFO", "MOD", "Uçuş modu talebi: " + name);
            }).start();
        }

        @JavascriptInterface
        public void setCruiseSpeed(final double speedMps) {
            new Thread(() -> {
                float s = (float) Math.max(0.5, Math.min(100.0, speedMps));
                sendCommandLong(178, 1f, s, -1f, 0f, 0f, 0f, 0f);
                emitLog("INFO", "HIZ", String.format(Locale.US, "Görev / uçuş hızı gönderildi: %.1f m/s", s));
            }).start();
        }

        @JavascriptInterface
        public void triggerServo(final int ch, final int pwm) {
            new Thread(() -> {
                sendCommandLong(183, (float) ch, (float) pwm, 0f, 0f, 0f, 0f, 0f);
                emitLog("INFO", "SERVO", "DO_SET_SERVO CH" + ch + " -> " + pwm + " us");
            }).start();
        }

        @JavascriptInterface
        public void setHomeCurrent() {
            new Thread(() -> {
                sendCommandLong(179, 1f, 0f, 0f, 0f, 0f, 0f, 0f);
                homeLat = lat;
                homeLon = lon;
                hasHome = true;
                emitLog("INFO", "HOME", "Home noktası mevcut araç konumu olarak güncellendi.");
            }).start();
        }

        @JavascriptInterface
        public void uploadMission(final String wpJson) {
            new Thread(() -> {
                try {
                    JSONArray arr = new JSONArray(wpJson);
                    int count = arr.length() + 1; // +1 for Home at seq 0
                    ByteBuffer mc = ByteBuffer.allocate(5).order(ByteOrder.LITTLE_ENDIAN);
                    mc.putShort((short) count);
                    mc.put((byte) targetSys);
                    mc.put((byte) targetComp);
                    mc.put((byte) 0);
                    sendRawFrame(encodeMavlinkV2(44, mc.array(), 221));
                    Thread.sleep(60);

                    // Seq 0 (Home)
                    sendMissionItemInt(0, 16, 0, (int) Math.round(homeLat * 1e7), (int) Math.round(homeLon * 1e7), 0f);
                    for (int i = 0; i < arr.length(); i++) {
                        JSONObject w = arr.getJSONObject(i);
                        int cmd = w.optInt("cmd", 16);
                        double wLat = w.optDouble("lat", lat);
                        double wLon = w.optDouble("lon", lon);
                        float wAlt = (float) w.optDouble("alt", 20.0);
                        sendMissionItemInt(i + 1, cmd, 6, (int) Math.round(wLat * 1e7), (int) Math.round(wLon * 1e7), wAlt);
                        Thread.sleep(25);
                    }
                    emitLog("NOTICE", "GÖREV", arr.length() + " waypoint araca yüklendi.");
                } catch (Exception ex) {
                    emitLog("WARNING", "GÖREV", "Görev yükleme hatası: " + ex.getMessage());
                }
            }).start();
        }

        @JavascriptInterface
        public void clearMissionOnVehicle() {
            new Thread(() -> {
                ByteBuffer bb = ByteBuffer.allocate(3).order(ByteOrder.LITTLE_ENDIAN);
                bb.put((byte) targetSys);
                bb.put((byte) targetComp);
                bb.put((byte) 0);
                sendRawFrame(encodeMavlinkV2(45, bb.array(), 232));
                emitLog("NOTICE", "GÖREV", "Araç üzerindeki görev temizlendi.");
            }).start();
        }

    void sendMissionItemInt(int seq, int command, int frame, int latInt, int lonInt, float alt) {
        ByteBuffer bb = ByteBuffer.allocate(38).order(ByteOrder.LITTLE_ENDIAN);
        bb.putFloat(0f); bb.putFloat(0f); bb.putFloat(0f); bb.putFloat(0f);
        bb.putInt(latInt);
        bb.putInt(lonInt);
        bb.putFloat(alt);
        bb.putShort((short) seq);
        bb.putShort((short) command);
        bb.put((byte) targetSys);
        bb.put((byte) targetComp);
        bb.put((byte) frame);
        bb.put((byte) (seq == 0 ? 1 : 0));
        bb.put((byte) 1); // autocontinue
        bb.put((byte) 0); // mission_type
        sendRawFrame(encodeMavlinkV2(73, bb.array(), 38));
    }

    void startHeartbeatLoop() {
        hbThread = new Thread(() -> {
            int tick = 0;
            while (connected.get()) {
                try {
                    pushTelemetryToUi();
                    if (tick % 8 == 0) {
                        sendGcsHeartbeat();
                    }
                    tick++;
                    Thread.sleep(125);
                } catch (Exception ignored) {
                }
            }
        }, "Tulpar-HB");
        hbThread.setDaemon(true);
        hbThread.start();
    }
}
