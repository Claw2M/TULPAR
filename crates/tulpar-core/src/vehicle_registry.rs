use tulpar_vehicle::Vehicle;

pub struct VehicleRegistry {
    vehicles: Vec<Box<dyn Vehicle>>,
}

impl VehicleRegistry {
    pub fn new() -> Self {
        Self {
            vehicles: Vec::new(),
        }
    }

    pub fn register(&mut self, vehicle: Box<dyn Vehicle>) {
        println!("[REGISTRY] Vehicle registered: {}", vehicle.name());
        self.vehicles.push(vehicle);
    }

    pub fn count(&self) -> usize {
        self.vehicles.len()
    }
}