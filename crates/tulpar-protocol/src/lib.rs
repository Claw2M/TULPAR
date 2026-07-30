#[derive(Debug, Clone, Copy)]
pub enum PacketType {
    Heartbeat = 0x01,
}

#[derive(Debug)]
pub struct Packet {
    pub magic: u16,
    pub version: u8,
    pub packet_type: PacketType,
    pub length: u16,
    pub payload: Vec<u8>,
    pub crc32: u32,
}


impl Packet {
    pub fn heartbeat() -> Self {
        Self {
            magic: 0x5450, // "TP"
            version: 1,
            packet_type: PacketType::Heartbeat,
            length: 0,
            payload: Vec::new(),
            crc32: 0,
        }
    }
}