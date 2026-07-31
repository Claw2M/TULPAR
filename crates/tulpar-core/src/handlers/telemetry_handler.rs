use crate::logger::Logger;

pub struct TelemetryHandler;

impl TelemetryHandler {
    pub fn handle() {
        Logger::info("Telemetry handled.");
    }
}