using CaeManager.Domain.Common;

namespace CaeManager.Application.Tenants.Encargo;

public static class ErroresEncargoAdministracion
{
    public static readonly Error ClausulaObligatoria = Error.Crear(
        "Encargo.ClausulaObligatoria", "Indica la cláusula del contrato que ampara el encargo de administración.");

    public static readonly Error ClausulaDemasiadoLarga = Error.Crear(
        "Encargo.ClausulaDemasiadoLarga", "La referencia a la cláusula del contrato es demasiado larga.");

    public static readonly Error OperacionNoValida = Error.Crear(
        "Encargo.OperacionNoValida",
        "El encargo de administración solo se registra a favor de un Operador CAE externo que gestione la organización entera y siga activo.");

    public static readonly Error VigenciaNoValida = Error.Crear(
        "Encargo.VigenciaNoValida", "La fecha de fin del encargo de administración tiene que ser posterior a hoy.");

    public static readonly Error YaRegistrado = Error.Crear(
        "Encargo.YaRegistrado",
        "Este Operador CAE externo ya tiene un encargo de administración sin retirar. Retíralo antes de registrar otro.");

    public static readonly Error NoEncontrado = Error.Crear(
        "Encargo.NoEncontrado", "No encontramos este encargo de administración.");

    public static readonly Error YaRetirado = Error.Crear(
        "Encargo.YaRetirado", "Este encargo de administración ya estaba retirado.");

    /// <summary>
    /// Lo devuelve <c>ExclusionesDelEncargoBehavior</c>: la petición pertenece a
    /// un área que el rol elevado abriría y el encargo no cubre.
    /// </summary>
    public static readonly Error ActoExcluido = Error.Crear(
        "Encargo.ActoExcluido",
        "Esta acción no forma parte del encargo de administración. Solo puede hacerla un Administrador propio de la organización.");
}
