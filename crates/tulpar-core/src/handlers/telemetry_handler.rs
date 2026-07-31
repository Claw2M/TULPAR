use crate::logger::Logger;

use tulpar_protocol::{Packet, Telemetry};

pub struct TelemetryHandler;

impl TelemetryHandler {
    pub fn handle(packet: Packet) {
        let telemetry = match Telemetry::from_payload(&packet.payload) {
            Some(data) => data,
            None => {
                Logger::error("Failed to decode telemetry.");
                return;
            }
        };

        Logger::info("========== TELEMETRY ==========");

        Logger::info(&format!(
            "Latitude   : {}",
            telemetry.latitude
        ));

        Logger::info(&format!(
            "Longitude  : {}",
            telemetry.longitude
        ));

        Logger::info(&format!(
            "Altitude   : {:.2} m",
            telemetry.altitude
        ));

        Logger::info(&format!(
            "Roll       : {:.2}",
            telemetry.roll
        ));

        Logger::info(&format!(
            "Pitch      : {:.2}",
            telemetry.pitch
        ));

        Logger::info(&format!(
            "Yaw        : {:.2}",
            telemetry.yaw
        ));

        Logger::info(&format!(
            "Battery    : {}%",
            telemetry.battery
        ));

        Logger::info(&format!(
            "Satellites : {}",
            telemetry.satellites
        ));
    }
}