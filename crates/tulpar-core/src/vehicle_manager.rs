use tulpar_vehicle::{BasicVehicle, Vehicle, VehicleType};

pub struct VehicleManager {
    vehicles: Vec<Box<dyn Vehicle>>,
}

impl VehicleManager {
    pub fn new() -> Self {
        Self {
            vehicles: Vec::new(),
        }
    }

    pub fn add_vehicle(&mut self, vehicle: Box<dyn Vehicle>) {
        println!("[MANAGER] Vehicle added: {}", vehicle.name());
        self.vehicles.push(vehicle);
    }

    pub fn vehicle_count(&self) -> usize {
        self.vehicles.len()
    }

    pub fn create_demo_vehicle(&mut self) {
        let mut drone = BasicVehicle::new(
            1,
            "Drone-01",
            VehicleType::UAV,
        );

        drone.connect();

        self.add_vehicle(Box::new(drone));
    }
}