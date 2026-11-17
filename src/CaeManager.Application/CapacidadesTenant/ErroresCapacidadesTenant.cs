using CaeManager.Domain.Common;

namespace CaeManager.Application.CapacidadesTenant;

public static class ErroresCapacidadesTenant
{
    public static readonly Error SoloAdministradorDelTenant = Error.Crear(
        "CapacidadTenant.SoloAdministradorDelTenant",
        "Solo el Administrador de la organización, operando la suya propia, puede conceder o revocar esta capacidad.");

    public static readonly Error BeneficiarioNoValido = Error.Crear(
        "CapacidadTenant.BeneficiarioNoValido",
        "La capacidad solo se concede a una cuenta activa de la propia organización con permiso de escritura.");

    public static readonly Error YaConcedida = Error.Crear(
        "CapacidadTenant.YaConcedida",
        "Esta cuenta ya tiene la capacidad concedida.");

    public static readonly Error NoVigente = Error.Crear(
        "CapacidadTenant.NoVigente",
        "Esta cuenta no tiene la capacidad concedida.");
}
