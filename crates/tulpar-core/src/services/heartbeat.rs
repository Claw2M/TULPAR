use std::future::Future;
use std::pin::Pin;
use std::sync::Arc;

use tokio::time::{sleep, Duration};

use crate::logger::Logger;
use crate::network_engine::NetworkEngine;
use crate::runtime::scheduler::Scheduler;
use crate::service::Service;

use tulpar_protocol::Packet;

pub struct HeartbeatService {
    network: Arc<NetworkEngine>,
}

impl HeartbeatService {
    pub fn new(network: Arc<NetworkEngine>) -> Self {
        Self { network }
    }
}

impl Service for HeartbeatService {
    fn name(&self) -> &'static str {
        "Heartbeat Service"
    }

    fn start(&mut self) -> Pin<Box<dyn Future<Output = ()> + Send>> {
        let network = self.network.clone();

        Box::pin(async move {
            Logger::info("Heartbeat Service started.");

            Scheduler::spawn(async move {
                let mut counter: u64 = 0;

                loop {
                    counter += 1;

                    let packet = Packet::heartbeat();
                    let bytes = packet.to_bytes();

                    network.send(&bytes, "127.0.0.1:14550");

                    Logger::info(&format!(
                        "Heartbeat #{} sent",
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