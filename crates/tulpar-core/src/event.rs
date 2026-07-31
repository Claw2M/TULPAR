#[derive(Debug, Clone)]
pub enum Event {
    CoreStarted,
    ConfigurationLoaded,
    VehicleConnected,
    VehicleDisconnected,
    PacketReceived,
    PacketSent,
}

pub struct EventBus;

impl EventBus {
    pub fn publish(event: Event) {
        println!("[EVENT] {:?}", event);
    }
}   