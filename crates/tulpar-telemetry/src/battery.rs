#[derive(Debug, Clone)]
pub struct BatteryTelemetry {
    pub voltage: f32,
    pub current: f32,
    pub remaining: u8,
}