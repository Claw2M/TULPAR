use crate::logger::Logger;

pub struct PacketDispatcher;

impl PacketDispatcher {
    pub fn new() -> Self {
        Self
    }

    pub fn dispatch(&self, packet: &[u8]) {
        if packet.len() < 4 {
            Logger::error("Invalid packet.");
            return;
        }

        let packet_type = packet[3];

        match packet_type {
            0x01 => {
                Logger::info("Heartbeat packet received.");
            }

            0x02 => {
                Logger::info("Telemetry packet received.");
            }

            0x03 => {
                Logger::info("Command packet received.");
            }

            _ => {
                Logger::warn("Unknown packet.");
            }
        }
    }
}