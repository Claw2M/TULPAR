use crate::event::{Event, EventBus};
use crate::logger::Logger;
use crate::network::dispatcher::PacketDispatcher;
use crate::network_engine::NetworkEngine;
use crate::service_manager::ServiceManager;
use crate::services::heartbeat::HeartbeatService;
use crate::state::app_state::{AppState, SharedState};
use crate::vehicle_manager::VehicleManager;
use crate::services::telemetry::TelemetryService;

use std::sync::{Arc, RwLock};

use tulpar_protocol::Packet;

pub struct Boot;

impl Boot {
    pub async fn start() {
        println!("=====================================");
        println!("       TULPAR Core v0.1.0-alpha");
        println!("=====================================");
        println!();

        println!("[BOOT] Initializing TULPAR Core...");

        // -------------------------------------------------
        // Shared Application State
        // -------------------------------------------------

        let state: SharedState = Arc::new(RwLock::new(AppState::new()));

        // -------------------------------------------------
        // Event Bus
        // -------------------------------------------------

        let event_bus = Arc::new(EventBus::new());

        event_bus.subscribe(|event| {
            Logger::info(&format!("EVENT => {:?}", event));
        });

        event_bus.publish(Event::CoreStarted);

        // -------------------------------------------------
        // Logger
        // -------------------------------------------------

        Logger::info("Logger initialized.");

        // -------------------------------------------------
        // Configuration
        // -------------------------------------------------

        Logger::info("Configuration loaded.");
        event_bus.publish(Event::ConfigurationLoaded);

        // -------------------------------------------------
        // Protocol
        // -------------------------------------------------

        let heartbeat = Packet::heartbeat();

        Logger::info("Protocol packet created: Heartbeat");

        let bytes = heartbeat.to_bytes();

        Logger::info(&format!("Packet Bytes: {:02X?}", bytes));

        // -------------------------------------------------
        // Network
        // -------------------------------------------------

        let network = Arc::new(NetworkEngine::new("0.0.0.0:0"));

        {
            let event_bus = event_bus.clone();

            network.start_receiver(move |packet| {
                let dispatcher = PacketDispatcher::new();
                dispatcher.dispatch(&packet);
            });
        }

        Logger::info("UDP Receiver started.");

        network.send(&bytes, "127.0.0.1:14550");

        event_bus.publish(Event::PacketSent);
        event_bus.publish(Event::HeartbeatSent);

        Logger::info("Heartbeat sent via UDP.");

        // -------------------------------------------------
        // Services
        // -------------------------------------------------

        let mut service_manager = ServiceManager::new();

        service_manager.register(Box::new(
            HeartbeatService::new(network.clone()),
        ));
        service_manager.register(Box::new(
            TelemetryService::new(network.clone()),
        ));

        Logger::info(&format!(
            "{} service(s) registered.",
            service_manager.count()
        ));

        service_manager.start_all().await;

        // -------------------------------------------------
        // Vehicles
        // -------------------------------------------------

        let mut vehicle_manager = VehicleManager::new();

        vehicle_manager.create_demo_vehicle();

        event_bus.publish(Event::VehicleConnected);

        Logger::info(&format!(
            "{} vehicle(s) loaded.",
            vehicle_manager.vehicle_count()
        ));

        // -------------------------------------------------
        // Shared State
        // -------------------------------------------------

        {
            let mut app = state.write().unwrap();

            app.connected = true;
            app.vehicle_count = vehicle_manager.vehicle_count();
        }

        {
            let app = state.read().unwrap();

            Logger::info(&format!(
                "State => connected={} vehicles={}",
                app.connected,
                app.vehicle_count
            ));
        }

        // -------------------------------------------------
        // Core
        // -------------------------------------------------

        Logger::info("Event Bus initialized.");
        Logger::info("Module Registry initialized.");

        println!();

        Logger::success("TULPAR Core is ready.");

        loop {
            tokio::time::sleep(std::time::Duration::from_secs(1)).await;
        }
    }
}