use crate::{Footer, Header, Packet, PacketType};

pub fn to_bytes(packet: &Packet) -> Vec<u8> {
    let mut bytes = Vec::new();

    bytes.extend_from_slice(&packet.header.magic);
    bytes.push(packet.header.version);
    bytes.push(packet.header.packet_type as u8);

    bytes.extend_from_slice(&packet.header.sequence.to_be_bytes());
    bytes.extend_from_slice(&packet.header.payload_size.to_be_bytes());

    bytes.extend_from_slice(&packet.payload);

    let footer = Footer::calculate(&bytes);

    bytes.extend_from_slice(&footer.crc32.to_be_bytes());

    bytes
}

pub fn from_bytes(bytes: &[u8]) -> Option<Packet> {
    if bytes.len() < 12 {
        return None;
    }

    if bytes[0] != 0x54 || bytes[1] != 0x50 {
        return None;
    }

    let version = bytes[2];

    let packet_type = match bytes[3] {
        0x01 => PacketType::Heartbeat,
        _ => return None,
    };

    let sequence =
        u16::from_be_bytes([bytes[4], bytes[5]]);

    let payload_size =
        u16::from_be_bytes([bytes[6], bytes[7]]);

    let payload_end = 8 + payload_size as usize;

    if bytes.len() < payload_end + 4 {
        return None;
    }

    let payload = bytes[8..payload_end].to_vec();

    let received_crc = u32::from_be_bytes([
        bytes[payload_end],
        bytes[payload_end + 1],
        bytes[payload_end + 2],
        bytes[payload_end + 3],
    ]);

    let footer = Footer {
        crc32: received_crc,
    };

    if !footer.verify(&bytes[..payload_end]) {
        return None;
    }

    Some(Packet {
        header: Header {
            magic: [0x54, 0x50],
            version,
            packet_type,
            sequence,
            payload_size,
        },
        payload,
        footer,
    })
}