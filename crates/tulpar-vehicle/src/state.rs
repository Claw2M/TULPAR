#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum VehicleState {
    Disconnected,
    Connecting,
    Connected,
    Armed,
    Flying,
    Landing,
    Mission,
    Failsafe,
}