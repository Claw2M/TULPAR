use crate::VehicleState;

pub trait Vehicle {
    fn id(&self) -> u32;

    fn name(&self) -> &str;

    fn state(&self) -> VehicleState;

    fn connect(&mut self);

    fn disconnect(&mut self);

    fn arm(&mut self);

    fn disarm(&mut self);

    fn takeoff(&mut self, altitude: f32);

    fn land(&mut self);
}