use crate::event::{Event, EventBus};
use crate::logger::Logger;

pub struct HeartbeatHandler;

impl HeartbeatHandler {
    pub fn handle(event_bus: &EventBus) {
        Logger::info("Heartbeat handled.");

        event_bus.publish(Event::HeartbeatReceived);
    }
}