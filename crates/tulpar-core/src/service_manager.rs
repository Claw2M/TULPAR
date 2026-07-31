use crate::service::Service;

pub struct ServiceManager {
    services: Vec<Box<dyn Service>>,
}

impl ServiceManager {
    pub fn new() -> Self {
        Self {
            services: Vec::new(),
        }
    }

    pub fn register(&mut self, service: Box<dyn Service>) {
        println!("[SERVICE] Registered {}", service.name());
        self.services.push(service);
    }

    pub async fn start_all(&mut self) {
        for service in self.services.iter_mut() {
            service.start().await;
        }
    }

    pub async fn stop_all(&mut self) {
        for service in self.services.iter_mut() {
            service.stop().await;
        }
    }

    pub fn count(&self) -> usize {
        self.services.len()
    }
}