use crate::event::Event;
use crate::logger::Logger;
use crate::network::dispatcher::PacketDispatcher;
use crate::service_manager::ServiceManager;
use crate::state::app_state::{AppState, SharedState};
use crate::vehicle_manager::VehicleManager;
use crate::network_engine::NetworkEngine;
use crate::services::heartbeat::HeartbeatService;

use std::sync::{Arc, RwLock};

use tulpar_network::UdpTransport;
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
        // Events
        // -------------------------------------------------

        println!("[EVENT] {:?}", Event::CoreStarted);

        // -------------------------------------------------
        // Logger
        // -------------------------------------------------

        Logger::info("Logger initialized.");

        // -------------------------------------------------
        // Configuration
        // -------------------------------------------------

        Logger::info("Configuration loaded.");

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

        let network = NetworkEngine::new("0.0.0.0:0");

        network.send(&bytes, "127.0.0.1:14550");

        Logger::info("Heartbeat sent via UDP.");

        // -------------------------------------------------
        // Packet Dispatcher
        // -------------------------------------------------

        let dispatcher = PacketDispatcher::new();

        dispatcher.dispatch(&bytes);

        // -------------------------------------------------
        // Services
        // -------------------------------------------------

        let mut service_manager = ServiceManager::new();

        service_manager.register(Box::new(HeartbeatService::new()));

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

        Logger::info(&format!(
            "{} vehicle(s) loaded.",
            vehicle_manager.vehicle_count()
        ));

        // -------------------------------------------------
        // Shared State Update
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
    }
}