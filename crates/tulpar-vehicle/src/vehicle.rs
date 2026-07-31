use crate::{VehicleState, VehicleType};
use tulpar_telemetry::VehicleTelemetry;

#[derive(Debug)]
pub struct BasicVehicle {
    id: u32,
    name: String,
    vehicle_type: VehicleType,
    state: VehicleState,
    telemetry: VehicleTelemetry,
}

impl BasicVehicle {
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
            telemetry: VehicleTelemetry::default(),
        }
    }

    pub fn id(&self) -> u32 {
        self.id
    }

    pub fn name(&self) -> &str {
        &self.name
    }

    pub fn vehicle_type(&self) -> VehicleType {
        self.vehicle_type
    }

    pub fn state(&self) -> VehicleState {
        self.state
    }

    pub fn set_state(&mut self, state: VehicleState) {
        self.state = state;
    }

    pub fn telemetry(&self) -> &VehicleTelemetry {
        &self.telemetry
    }

    pub fn telemetry_mut(&mut self) -> &mut VehicleTelemetry {
        &mut self.telemetry
    }
}