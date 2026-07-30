use crate::{VehicleState, VehicleType};

#[derive(Debug)]
pub struct Vehicle {
    pub id: u32,
    pub name: String,
    pub vehicle_type: VehicleType,
    pub state: VehicleState,
}

impl Vehicle {
    pub fn new(
        id: u32,
        name: &str,
        vehicle_type: VehicleType,
    ) -> Self {
        Self {
            id,
            name: name.to_string(),
            vehicle_type,
            state: VehicleState::Disconnected,
        }
    }

    pub fn connect(&mut self) {
        self.state = VehicleState::Connected;
    }

    pub fn arm(&mut self) {
        self.state = VehicleState::Armed;
    }

    pub fn takeoff(&mut self) {
        self.state = VehicleState::Flying;
    }

    pub fn land(&mut self) {
        self.state = VehicleState::Landing;
    }

    pub fn disconnect(&mut self) {
        self.state = VehicleState::Disconnected;
    }
}