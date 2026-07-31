use crate::logger::Logger;

pub struct HeartbeatHandler;

impl HeartbeatHandler {
    pub fn handle() {
        Logger::info("Heartbeat handled.");
    }
}