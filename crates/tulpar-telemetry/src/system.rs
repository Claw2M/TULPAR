#[derive(Debug, Clone)]
pub struct SystemTelemetry {
    pub armed: bool,
    pub connected: bool,
    pub flight_mode: String,
}