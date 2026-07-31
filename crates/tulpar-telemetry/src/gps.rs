#[derive(Debug, Clone)]
pub struct GpsTelemetry {
    pub latitude: f64,
    pub longitude: f64,
    pub altitude: f32,
    pub satellites: u8,
}