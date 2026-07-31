use crate::{Footer, Header, PacketType};

#[derive(Debug, Clone)]
pub struct Packet {
    pub header: Header,
    pub payload: Vec<u8>,
    pub footer: Footer,
}

impl Packet {
    pub fn heartbeat() -> Self {
        Self {
            header: Header::new(
                PacketType::Heartbeat,
                1,
                0,
            ),
            payload: Vec::new(),
            footer: Footer::new(),
        }
    }

    pub fn to_bytes(&self) -> Vec<u8> {
        let mut bytes = Vec::new();

        // Magic
        bytes.extend_from_slice(&self.header.magic);

        // Version
        bytes.push(self.header.version);

        // Packet Type
        bytes.push(self.header.packet_type as u8);

        // Sequence
        bytes.extend_from_slice(&self.header.sequence.to_le_bytes());

        // Payload Size
        bytes.extend_from_slice(&self.header.payload_size.to_le_bytes());

        // Payload
        bytes.extend_from_slice(&self.payload);

        // CRC (şimdilik sahte)
        bytes.push(0x00);

        bytes
    }

    pub fn from_bytes(bytes: &[u8]) -> Option<Self> {
        if bytes.len() < 9 {
            return None;
        }

        if bytes[0] != 0x54 || bytes[1] != 0x50 {
            return None;
        }

        let packet_type = match bytes[3] {
            0x01 => PacketType::Heartbeat,
            _ => return None,
        };

        let sequence =
            u16::from_le_bytes([bytes[4], bytes[5]]);

        let payload_size =
            u16::from_le_bytes([bytes[6], bytes[7]]);

        let payload_size = payload_size as usize;

        if bytes.len() < 9 + payload_size {
            return None;
        }

        let payload =
            bytes[8..8 + payload_size].to_vec();

        Some(Self {
            header: Header::new(
                packet_type,
                sequence,
                payload_size as u16,
            ),
            payload,
            footer: Footer::new(),
        })
    }
}