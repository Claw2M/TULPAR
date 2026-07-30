use crate::config::Config;
use crate::logger::Logger;
use crate::event::Event;

use tulpar_protocol::Packet;

pub struct Boot;

impl Boot {
    pub fn start() {
        println!("[BOOT] Initializing TULPAR Core...");

        let heartbeat = Packet::heartbeat();

        Logger::info(&format!("Event: {:?}", Event::CoreStarted));
        Logger::info("Logger initialized.");

        Logger::info(&format!(
            "Protocol packet created: {:?}",
            heartbeat.packet_type
        ));

        Config::load();

        Logger::info("Event Bus initialized.");
        Logger::info("Module Registry initialized.");

        println!();

        Logger::success("TULPAR Core is ready.");
    }
}