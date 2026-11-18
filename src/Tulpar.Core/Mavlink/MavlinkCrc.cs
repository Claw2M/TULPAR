namespace Tulpar.Core.Mavlink;

/// <summary>
/// Implements MAVLink X.25 (MCRF4XX) CRC-16 computation and standard CRC_EXTRA lookup.
/// </summary>
public static class MavlinkCrc
{
    /// <summary>
    /// Computes the 16-bit MAVLink X.25 checksum over <paramref name="data"/> followed by <paramref name="crcExtra"/>.
    /// </summary>
    public static ushort Compute(ReadOnlySpan<byte> data, byte crcExtra)
    {
        ushort crc = 0xFFFF;
        foreach (byte b in data)
        {
            crc = Accumulate(b, crc);
        }

        return Accumulate(crcExtra, crc);
    }

    /// <summary>
    /// Accumulates a single byte into a running X.25 CRC-16 value.
    /// </summary>
    public static ushort Accumulate(byte value, ushort crc)
    {
        byte tmp = (byte)(value ^ (crc & 0xFF));
        tmp ^= (byte)(tmp << 4);
        return (ushort)((crc >> 8) ^ (tmp << 8) ^ (tmp << 3) ^ (tmp >> 4));
    }

    /// <summary>
    /// Resolves the official MAVLink CRC_EXTRA seed byte for a known message ID.
    /// </summary>
    public static bool TryGetCrcExtra(uint messageId, out byte crcExtra)
    {
        switch (messageId)
        {
            case 0: crcExtra = 50; return true;     // HEARTBEAT
            case 1: crcExtra = 124; return true;    // SYS_STATUS
            case 2: crcExtra = 137; return true;    // SYSTEM_TIME
            case 11: crcExtra = 89; return true;    // SET_MODE
            case 20: crcExtra = 214; return true;   // PARAM_REQUEST_READ
            case 21: crcExtra = 159; return true;   // PARAM_REQUEST_LIST
            case 22: crcExtra = 220; return true;   // PARAM_VALUE
            case 23: crcExtra = 168; return true;   // PARAM_SET
            case 24: crcExtra = 24; return true;    // GPS_RAW_INT
            case 30: crcExtra = 39; return true;    // ATTITUDE
            case 33: crcExtra = 104; return true;   // GLOBAL_POSITION_INT
            case 39: crcExtra = 254; return true;   // MISSION_ITEM
            case 40: crcExtra = 230; return true;   // MISSION_REQUEST
            case 41: crcExtra = 28; return true;    // MISSION_SET_CURRENT
            case 42: crcExtra = 28; return true;    // MISSION_CURRENT
            case 43: crcExtra = 132; return true;   // MISSION_REQUEST_LIST
            case 44: crcExtra = 221; return true;   // MISSION_COUNT
            case 45: crcExtra = 232; return true;   // MISSION_CLEAR_ALL
            case 46: crcExtra = 11; return true;    // MISSION_ITEM_REACHED
            case 47: crcExtra = 153; return true;   // MISSION_ACK
            case 51: crcExtra = 196; return true;   // MISSION_REQUEST_INT
            case 65: crcExtra = 118; return true;   // RC_CHANNELS
            case 66: crcExtra = 148; return true;   // REQUEST_DATA_STREAM
            case 73: crcExtra = 38; return true;    // MISSION_ITEM_INT
            case 74: crcExtra = 20; return true;    // VFR_HUD
            case 76: crcExtra = 152; return true;   // COMMAND_LONG
            case 77: crcExtra = 143; return true;   // COMMAND_ACK
            case 86: crcExtra = 5; return true;     // SET_POSITION_TARGET_GLOBAL_INT
            case 109: crcExtra = 185; return true;  // RADIO_STATUS
            case 147: crcExtra = 154; return true;  // BATTERY_STATUS
            case 193: crcExtra = 71; return true;   // EKF_STATUS_REPORT
            case 241: crcExtra = 90; return true;   // VIBRATION
            case 242: crcExtra = 104; return true;  // HOME_POSITION
            case 245: crcExtra = 130; return true;  // EXTENDED_SYS_STATE
            case 253: crcExtra = 83; return true;   // STATUSTEXT
            default:
                crcExtra = 0;
                return false;
        }
    }
}
