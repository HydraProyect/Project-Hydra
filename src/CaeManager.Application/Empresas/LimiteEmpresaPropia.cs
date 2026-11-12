using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Empresas;

/// <summary>
/// Decisión de producto D-P11 (2026-10-30, ajustada el 2026-11-12): todo Tenant propietario tiene una sola Empresa
/// propia (<c>EsPropia = true</c>), sea cual sea su Producto Contratado. Es la misma comprobación para todos los
/// escritores de una Empresa propia: el alta unitaria, las tres importaciones y las restauraciones desde la
/// Papelera. Una prueba de arquitectura exige que toda llamada al constructor público de <c>Empresa</c> fuera de
/// Domain y de los seeders esté en un handler que llame a <see cref="ComprobarAltaAsync"/>.
/// <para>
/// Los Clientes empresariales y las Subcontratas (<c>EsPropia = false</c>) no cuentan ni tienen límite.
/// </para>
/// <para>
/// Solo impide crear: no hay índice único ni migración, porque puede haber Tenants que ya tengan más de una y
/// nadie lo ha medido. Cuenta las Empresas propias activas; las de la Papelera no cuentan, y por eso restaurar
/// una también pasa por aquí. La excepción de la decisión, una ampliación de grupo aprobada por Soporte TALVEG,
/// no existe todavía en el código: hasta que exista, el rechazo es total.
/// </para>
/// </summary>
public interface ILimiteEmpresaPropia
{
    /// <summary>
    /// <c>null</c> si el Tenant actual puede tener una Empresa propia activa más; si no, el error que se muestra.
    /// <paramref name="propiasAnadidasSinGuardar"/> son las Empresas propias que la misma operación ya ha añadido
    /// y aún no ha guardado (una importación con varias filas). Con <paramref name="esIntento"/> el rechazo avisa
    /// a Soporte TALVEG; una vista previa, o una fila más de una importación que ya avisó, pasa <c>false</c>.
    /// </summary>
    Task<Error?> ComprobarAltaAsync(int propiasAnadidasSinGuardar, bool esIntento, CancellationToken cancellationToken = default);
}

public static class ErroresLimiteEmpresaPropia
{
    /// <summary>Empresas propias activas que puede tener un Tenant propietario.</summary>
    public const int MaximoEmpresasPropias = 1;

    public static readonly Error UnaSolaEmpresaPropia = Error.Crear(
        "Empresa.LimiteEmpresaPropia",
        // Neutro respecto al Producto Contratado (el Tenant TEC participante no tiene ninguno, EP-1) y sin prometer la
        // ampliación de grupo, que todavía no existe. Sirve también para restaurar desde la Papelera.
        "Tu organización ya tiene su Empresa propia y solo puede tener una activa. Si la empresa es de otra "
        + "organización, regístrala como Cliente o como Subcontrata: no cuentan para este límite.");
}

public sealed class LimiteEmpresaPropia(
    IEmpresasQueryContext empresasContext,
    ITenantActual tenantActual,
    IAlertaOperativa alertaOperativa)
    : ILimiteEmpresaPropia
{
    public async Task<Error?> ComprobarAltaAsync(
        int propiasAnadidasSinGuardar, bool esIntento, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(propiasAnadidasSinGuardar);

        // El filtro global ya deja solo las activas del Tenant actual, y RLS repite el Tenant. Las dos condiciones van
        // también escritas, para que el recuento no dependa de ellos: restaurar lee la Empresa de la Papelera con
        // IgnoreQueryFilters en el mismo contexto, y la que se está restaurando no puede contarse a sí misma. Sin
        // Tenant actual no hay nada que contar, y el escritor fallaría después al sellar la fila.
        var tenantId = tenantActual.TenantId;
        var activas = await empresasContext.Empresas.CountAsync(
            e => e.TenantId == tenantId && e.EsPropia && !e.EstaEliminado, cancellationToken);
        if (activas + propiasAnadidasSinGuardar < ErroresLimiteEmpresaPropia.MaximoEmpresasPropias)
            return null;

        if (esIntento)
        {
            // Aviso a Soporte TALVEG por el canal de alertas operativas que ya existe. Sin razón social ni CIF de la
            // empresa intentada: basta el Tenant para que Soporte TALVEG lo mire.
            alertaOperativa.Emitir(
                $"Límite de Empresa propia (D-P11): se ha rechazado dar de alta o restaurar otra Empresa propia en el "
                + $"Tenant {tenantId}, que ya tiene una activa.",
                NivelAlertaOperativa.Aviso);
        }

        return ErroresLimiteEmpresaPropia.UnaSolaEmpresaPropia;
    }
}
