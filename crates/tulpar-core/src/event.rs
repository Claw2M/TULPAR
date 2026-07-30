#[derive(Debug)]
pub enum Event {
    CoreStarted,
    ConfigurationLoaded,
    VehicleConnected,
    VehicleDisconnected,
}