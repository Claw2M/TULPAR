use std::sync::{Arc, RwLock};

#[derive(Debug)]
pub struct AppState {
    pub connected: bool,
    pub vehicle_count: usize,
    pub heartbeat_count: u64,
}

impl AppState {
    pub fn new() -> Self {
        Self {
            connected: false,
            vehicle_count: 0,
            heartbeat_count: 0,
        }
    }
}

pub type SharedState = Arc<RwLock<AppState>>;