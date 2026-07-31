mod boot;
mod config;
mod event;
mod logger;
mod service;
mod service_manager;
mod vehicle_manager;
mod vehicle_registry;
mod network_engine;

mod runtime;
mod services;
mod network;
mod state;
mod handlers;

#[tokio::main]
async fn main() {
    boot::Boot::start().await;
}