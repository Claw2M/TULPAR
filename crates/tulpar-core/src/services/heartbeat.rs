use std::future::Future;
use std::pin::Pin;
use crate::runtime::scheduler::Scheduler;
use tokio::time::{sleep, Duration};

use crate::logger::Logger;
use crate::service::Service;

pub struct HeartbeatService;

impl HeartbeatService {
    pub fn new() -> Self {
        Self
    }
}

impl Service for HeartbeatService {
    fn name(&self) -> &'static str {
        "Heartbeat Service"
    }

    fn start(&mut self) -> Pin<Box<dyn Future<Output = ()> + Send>> {
        Box::pin(async move {
            Logger::info("Heartbeat Service started.");

            Scheduler::spawn(async move {
                let mut counter: u64 = 0;

                loop {
                    counter += 1;

                    Logger::info(&format!(
                        "Heartbeat #{}",
                        counter
                    ));

                    sleep(Duration::from_secs(1)).await;
                }
            });
        })
    }

    fn stop(&mut self) -> Pin<Box<dyn Future<Output = ()> + Send>> {
        Box::pin(async move {
            Logger::info("Heartbeat Service stopped.");
        })
    }
}