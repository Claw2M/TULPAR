use crate::{BasicVehicle, Vehicle, VehicleState};

impl Vehicle for BasicVehicle {
    fn id(&self) -> u32 {
        self.id()
    }

    fn name(&self) -> &str {
        self.name()
    }

    fn state(&self) -> VehicleState {
        self.state()
    }

    fn connect(&mut self) {
        self.set_state(VehicleState::Connected);
    }

    fn disconnect(&mut self) {
        self.set_state(VehicleState::Disconnected);
    }

    fn arm(&mut self) {
        self.set_state(VehicleState::Armed);
    }

    fn disarm(&mut self) {
        self.set_state(VehicleState::Connected);
    }

    fn takeoff(&mut self, _: f32) {
        self.set_state(VehicleState::Flying);
    }

    fn land(&mut self) {
        self.set_state(VehicleState::Landing);
    }
}