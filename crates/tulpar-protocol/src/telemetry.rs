#[derive(Debug, Clone)]
pub struct Telemetry {
    
    pub latitude: f64,
    pub longitude: f64,
    pub altitude: f32,

    pub roll: f32,
    pub pitch: f32,
    pub yaw: f32,

    pub battery: u8,
    pub satellites: u8,
    
}
impl Telemetry {
    pub fn from_payload(payload: &[u8]) -> Option<Self> {
        if payload.len() != 34 {
            return None;
        }

        Some(Self {
            latitude: f64::from_le_bytes(payload[0..8].try_into().ok()?),
            longitude: f64::from_le_bytes(payload[8..16].try_into().ok()?),
            altitude: f32::from_le_bytes(payload[16..20].try_into().ok()?),

            roll: f32::from_le_bytes(payload[20..24].try_into().ok()?),
            pitch: f32::from_le_bytes(payload[24..28].try_into().ok()?),
            yaw: f32::from_le_bytes(payload[28..32].try_into().ok()?),

            battery: payload[32],
            satellites: payload[33],
        })
    }
}