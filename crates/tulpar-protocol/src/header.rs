use crate::PacketType;

#[derive(Debug, Clone)]
pub struct Header {
    pub magic: [u8; 2],
    pub version: u8,
    pub packet_type: PacketType,
    pub sequence: u16,
    pub payload_size: u16,
}

impl Header {
    pub fn new(
        packet_type: PacketType,
        sequence: u16,
        payload_size: u16,
    ) -> Self {
        Self {
            magic: [0x54, 0x50],
            version: 1,
            packet_type,
            sequence,
            payload_size,
        }
    }
}