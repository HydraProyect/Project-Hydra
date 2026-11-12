using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Empresas;

/// <summary>
/// Decisión de producto D-P11 (2026-10-30, ajustada el 2026-11-12): todo Tenant propietario tiene una sola Empresa
/// propia (<c>EsPropia = true</c>), sea cual sea su Producto Contratado. Es la misma comprobación para todos los
/// escritores de una Empresa propia: el alta unitaria, las tres importaciones y las restauraciones desde la
/// Papelera. Una prueba de arquitectura exige que todo método fuera de Domain y de los seeders que construya una
/// <c>Empresa</c> con su constructor público, o restaure una de la Papelera, llame a <see cref="ComprobarAltaAsync"/>
/// o a <see cref="ComprobarGuardadoAsync"/>, y que su clase guarde dentro de <see cref="SerializarAsync"/>.
/// <para>
/// Los Clientes empresariales y las Subcontratas (<c>EsPropia = false</c>) no cuentan ni tienen límite.
/// </para>
/// <para>
/// Solo impide crear: no hay índice único ni migración, porque puede haber Tenants que ya tengan más de una y
/// nadie lo ha medido (un índice único haría fallar la migración al desplegar). Cuenta las Empresas propias activas;
/// las de la Papelera no cuentan, y por eso restaurar una también pasa por aquí. La excepción de la decisión, una
/// ampliación de grupo aprobada por Soporte TALVEG, no existe todavía en el código: hasta que exista, el rechazo es
/// total.
/// </para>
/// <para>
/// Recuento y escritura van en dos sentencias: sin más, dos altas concurrentes del mismo Tenant propietario, o un alta
/// y una restauración, contarían cero las dos y dejarían dos Empresas propias (revisión Codex r1 de la PR #2193, ALTO
/// 1). Por eso el recuento que decide se hace bajo <see cref="SerializarAsync"/>, un candado de transacción por Tenant
/// propietario que dura hasta el COMMIT del guardado. Orden de candados: quien tome también el de las altas con
/// acreditación (<c>ISerializacionAltasAcreditacionEmpresa</c>) lo toma antes que este.
/// </para>
/// </summary>
public interface ILimiteEmpresaPropia
{
    /// <summary>
    /// <c>null</c> si el Tenant actual puede tener una Empresa propia activa más; si no, el error que se muestra.
    /// <paramref name="propiasAnadidasSinGuardar"/> son las Empresas propias que la misma operación ya ha añadido
    /// y aún no ha guardado (una importación con varias filas). Con <paramref name="esIntento"/> el rechazo avisa
    /// a Soporte TALVEG; una vista previa, o una fila más de una importación que ya avisó, pasa <c>false</c>.
    /// Fuera de <see cref="SerializarAsync"/> solo anticipa la respuesta: otra operación puede guardar entre este
    /// recuento y el guardado propio.
    /// </summary>
    Task<Error?> ComprobarAltaAsync(int propiasAnadidasSinGuardar, bool esIntento, CancellationToken cancellationToken = default);

    /// <summary>
    /// La comprobación final de quien va a guardar <paramref name="propiasSinGuardar"/> Empresas propias nuevas o
    /// restauradas: <c>null</c> si caben todas. Se llama dentro de <see cref="SerializarAsync"/>, justo antes de
    /// guardar; un rechazo aquí es una carrera perdida y avisa a Soporte TALVEG.
    /// </summary>
    Task<Error?> ComprobarGuardadoAsync(int propiasSinGuardar, CancellationToken cancellationToken = default);

    /// <summary>
    /// Ejecuta <paramref name="operacion"/> con el candado de Empresa propia del Tenant actual, dentro de la transacción
    /// en curso o de una nueva que se confirma si la operación tiene éxito y se deshace si falla. Todo recuento que
    /// haga la operación con <see cref="ComprobarAltaAsync"/> o <see cref="ComprobarGuardadoAsync"/> ve lo que otra
    /// operación del mismo Tenant propietario ya confirmó, y nadie más guarda una Empresa propia hasta el COMMIT. No se
    /// reintenta: un intento fallido descarta lo rastreado.
    /// </summary>
    Task<Result> SerializarAsync(Func<CancellationToken, Task<Result>> operacion, CancellationToken cancellationToken = default);
}

/// <summary>
/// El candado de transacción por Tenant propietario de <see cref="ILimiteEmpresaPropia.SerializarAsync"/>
/// (<c>pg_advisory_xact_lock</c>, espacio de clave <c>app.limite_empresa_propia:</c> + TenantId). Lo implementa
/// Infrastructure sobre la conexión y la transacción del contexto que guarda.
/// </summary>
public interface ICandadoEmpresaPropia
{
    Task<Result> EjecutarAsync(Func<CancellationToken, Task<Result>> operacion, CancellationToken cancellationToken = default);
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
    IAlertaOperativa alertaOperativa,
    ICandadoEmpresaPropia candado)
    : ILimiteEmpresaPropia
{
    public Task<Result> SerializarAsync(Func<CancellationToken, Task<Result>> operacion, CancellationToken cancellationToken = default) =>
        candado.EjecutarAsync(operacion, cancellationToken);

    public Task<Error?> ComprobarGuardadoAsync(int propiasSinGuardar, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(propiasSinGuardar);
        // Caben N si caben N - 1 sin guardar y una más.
        return propiasSinGuardar == 0
            ? Task.FromResult<Error?>(null)
            : ComprobarAltaAsync(propiasSinGuardar - 1, esIntento: true, cancellationToken);
    }

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
