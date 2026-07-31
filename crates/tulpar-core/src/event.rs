use std::sync::{
    Arc, Mutex,
};

#[derive(Debug, Clone)]
pub enum Event {
    CoreStarted,

    ConfigurationLoaded,

    VehicleConnected,

    VehicleDisconnected,

    PacketReceived,

    PacketSent,

    HeartbeatReceived,

    HeartbeatSent,
}

type Callback = Arc<dyn Fn(Event) + Send + Sync>;

pub struct EventBus {
    listeners: Mutex<Vec<Callback>>,
}

impl EventBus {
    pub fn new() -> Self {
        Self {
            listeners: Mutex::new(Vec::new()),
        }
    }

    pub fn subscribe<F>(&self, callback: F)
    where
        F: Fn(Event) + Send + Sync + 'static,
    {
        self.listeners
            .lock()
            .unwrap()
            .push(Arc::new(callback));
    }

    pub fn publish(&self, event: Event) {
        let listeners = self.listeners.lock().unwrap();

        for listener in listeners.iter() {
            listener(event.clone());
        }
    }
}