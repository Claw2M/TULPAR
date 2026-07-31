#[derive(Debug, Clone, Copy)]
#[repr(u8)]
pub enum PacketType {
    Heartbeat = 0x01,
}