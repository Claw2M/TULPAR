#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum VehicleType {
    UAV,
    UGV,
    USV,
    UUV,
    Simulator,
}