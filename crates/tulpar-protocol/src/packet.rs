use crate::{
    Footer,
    Header,
    PacketType,
    Telemetry,
};

#[derive(Debug, Clone)]
pub struct Packet {
    pub header: Header,
    pub payload: Vec<u8>,
    pub footer: Footer,
}

impl Packet {
    /// Heartbeat paketi
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

    /// Telemetry paketi
    pub fn telemetry(data: Telemetry) -> Self {
        let mut payload = Vec::new();

        // GPS
        payload.extend_from_slice(&data.latitude.to_le_bytes());
        payload.extend_from_slice(&data.longitude.to_le_bytes());
        payload.extend_from_slice(&data.altitude.to_le_bytes());

        // Attitude
        payload.extend_from_slice(&data.roll.to_le_bytes());
        payload.extend_from_slice(&data.pitch.to_le_bytes());
        payload.extend_from_slice(&data.yaw.to_le_bytes());

        // Battery
        payload.push(data.battery);

        // Satellites
        payload.push(data.satellites);

        Self {
            header: Header::new(
                PacketType::Telemetry,
                1,
                payload.len() as u16,
            ),
            payload,
            footer: Footer::new(),
        }
    }

    /// Packet -> Bytes
    pub fn to_bytes(&self) -> Vec<u8> {
        let mut bytes = Vec::new();

        // Header
        bytes.extend_from_slice(&self.header.magic);
        bytes.push(self.header.version);
        bytes.push(self.header.packet_type as u8);
        bytes.extend_from_slice(&self.header.sequence.to_le_bytes());
        bytes.extend_from_slice(&self.header.payload_size.to_le_bytes());

        // Payload
        bytes.extend_from_slice(&self.payload);

        // CRC (şimdilik dummy)
        bytes.push(0x00);

        bytes
    }

    /// Bytes -> Packet
    pub fn from_bytes(bytes: &[u8]) -> Option<Self> {
        if bytes.len() < 9 {
            return None;
        }

        if bytes[0] != 0x54 || bytes[1] != 0x50 {
            return None;
        }

        let packet_type = match bytes[3] {
            0x01 => PacketType::Heartbeat,
            0x02 => PacketType::Telemetry,
            0x03 => PacketType::Command,
            _ => return None,
        };

        let sequence =
            u16::from_le_bytes([bytes[4], bytes[5]]);

        let payload_size =
            u16::from_le_bytes([bytes[6], bytes[7]]) as usize;

        if bytes.len() < 8 + payload_size + 1 {
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