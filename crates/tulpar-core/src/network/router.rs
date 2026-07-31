use crate::handlers::{
    heartbeat_handler::HeartbeatHandler,
    telemetry_handler::TelemetryHandler,
};

use tulpar_protocol::{Packet, PacketType};

pub struct PacketRouter;

impl PacketRouter {
    pub fn route(packet: Packet) {
        match packet.header.packet_type {
            PacketType::Heartbeat => {
                HeartbeatHandler::handle();
            }

            PacketType::Telemetry => {
                TelemetryHandler::handle(packet);
            }

            PacketType::Command => {
                println!("[ROUTER] Command");
            }
        }
    }
}