use std::future::Future;
use std::pin::Pin;
use std::sync::Arc;

use tokio::time::{sleep, Duration};

use crate::logger::Logger;
use crate::network_engine::NetworkEngine;
use crate::runtime::scheduler::Scheduler;
use crate::service::Service;

use tulpar_protocol::{Packet, Telemetry};

pub struct TelemetryService {
    network: Arc<NetworkEngine>,
}

impl TelemetryService {
    pub fn new(network: Arc<NetworkEngine>) -> Self {
        Self { network }
    }
}

impl Service for TelemetryService {
    fn name(&self) -> &'static str {
        "Telemetry Service"
    }

    fn start(&mut self) -> Pin<Box<dyn Future<Output = ()> + Send>> {
        let network = self.network.clone();

        Box::pin(async move {
            Logger::info("Telemetry Service started.");

            Scheduler::spawn(async move {
                loop {
                    let telemetry = Telemetry {
                        latitude: 41.015137,
                        longitude: 28.979530,
                        altitude: 125.4,

                        roll: 0.5,
                        pitch: -1.2,
                        yaw: 182.0,

                        battery: 97,
                        satellites: 14,
                    };

                    let packet = Packet::telemetry(telemetry);

                    network.send(
                        &packet.to_bytes(),
                        "127.0.0.1:14550",
                    );

                    Logger::info("Telemetry packet sent.");

                    sleep(Duration::from_millis(500)).await;
                }
            });
        })
    }

    fn stop(&mut self) -> Pin<Box<dyn Future<Output = ()> + Send>> {
        Box::pin(async {
            Logger::info("Telemetry Service stopped.");
        })
    }
}