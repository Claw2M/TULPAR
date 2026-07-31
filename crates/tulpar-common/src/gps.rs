#[derive(Debug, Clone, Copy)]
pub struct GpsPosition {
    pub latitude: f64,
    pub longitude: f64,
    pub altitude: f32,
}

impl GpsPosition {
    pub fn new(latitude: f64, longitude: f64, altitude: f32) -> Self {
        Self {
            latitude,
            longitude,
            altitude,
        }
    }
}