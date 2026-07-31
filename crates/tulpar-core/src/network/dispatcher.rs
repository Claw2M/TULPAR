use crate::logger::Logger;
use crate::network::router::PacketRouter;

use tulpar_protocol::Packet;

pub struct PacketDispatcher;

impl PacketDispatcher {
    pub fn new() -> Self {
        Self
    }

    pub fn dispatch(&self, bytes: &[u8]) {
        let packet = match Packet::from_bytes(bytes) {
            Some(packet) => packet,
            None => {
                Logger::error("Failed to parse packet.");
                return;
            }
        };

        PacketRouter::route(packet);
    }
}