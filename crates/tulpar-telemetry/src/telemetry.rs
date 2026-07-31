use crate::{
    AttitudeTelemetry,
    BatteryTelemetry,
    GpsTelemetry,
    PositionTelemetry,
    SystemTelemetry,
    VelocityTelemetry,
};

#[derive(Debug, Clone)]
pub struct VehicleTelemetry {
    pub battery: BatteryTelemetry,
    pub gps: GpsTelemetry,
    pub attitude: AttitudeTelemetry,
    pub position: PositionTelemetry,
    pub velocity: VelocityTelemetry,
    pub system: SystemTelemetry,
}

impl Default for VehicleTelemetry {
    fn default() -> Self {
        Self {
            battery: BatteryTelemetry {
                voltage: 0.0,
                current: 0.0,
                remaining: 0,
            },
            gps: GpsTelemetry {
                latitude: 0.0,
                longitude: 0.0,
                altitude: 0.0,
                satellites: 0,
            },
            attitude: AttitudeTelemetry {
                roll: 0.0,
                pitch: 0.0,
                yaw: 0.0,
            },
            position: PositionTelemetry {
                north: 0.0,
                east: 0.0,
                down: 0.0,
            },
            velocity: VelocityTelemetry {
                x: 0.0,
                y: 0.0,
                z: 0.0,
            },
            system: SystemTelemetry {
                armed: false,
                connected: false,
                flight_mode: "Unknown".to_string(),
            },
        }
    }
}