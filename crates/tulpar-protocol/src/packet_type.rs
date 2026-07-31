#[derive(Debug, Clone, Copy, PartialEq, Eq)]
#[repr(u8)]
pub enum PacketType {
    /// Core heartbeat
    Heartbeat = 0x01,

    /// Telemetry data
    Telemetry = 0x02,

    /// Command packet
    Command = 0x03,
}