using CaeManager.Application.Common;
using CaeManager.Application.Empresas;
using CaeManager.Application.Tests.Common;
using CaeManager.Application.Tests.Documentos;

namespace CaeManager.Application.Tests.Empresas;

/// <summary>Límite de D-P11 real con dobles en memoria, para los handlers que lo reciben.</summary>
public static class LimitesDePrueba
{
    /// <summary>Tenant propietario sin ninguna Empresa propia todavía: el límite permite la primera.</summary>
    public static LimiteEmpresaPropia SinEmpresaPropia() =>
        new(new EmpresasQueryContextFalso(), new TenantActualFijo(Guid.NewGuid()), new AlertaOperativaFalsa(), new CandadoEnMemoria());

    /// <summary>
    /// El límite sobre <paramref name="empresas"/>, el mismo contexto que lee el handler. Sin
    /// <paramref name="tenantId"/>, el Tenant actual es <see cref="Guid.Empty"/>, el que tiene una Empresa construida
    /// en el test sin sellar: el doble de contexto no filtra por Tenant y el límite sí.
    /// </summary>
    public static LimiteEmpresaPropia Sobre(
        EmpresasQueryContextFalso empresas, AlertaOperativaFalsa alertas, Guid? tenantId = null, CandadoEnMemoria? candado = null) =>
        new(empresas, new TenantActualFijo(tenantId ?? Guid.Empty), alertas, candado ?? new CandadoEnMemoria());

    /// <summary>
    /// Sin base de datos no hay candado que tomar: ejecuta la operación y anota cuántas veces se pidió y si el recuento
    /// del límite ocurrió dentro. El candado real se prueba en <c>CandadoEmpresaPropiaTests</c> (integración).
    /// </summary>
    public sealed class CandadoEnMemoria : ICandadoEmpresaPropia
    {
        public int Ejecuciones { get; private set; }
        public bool Dentro { get; private set; }

        /// <summary>
        /// Lo que otra operación del mismo Tenant confirmó mientras esta esperaba el candado: se ejecuta al tomarlo,
        /// antes de la operación. Si el escritor contara fuera del candado, no lo vería.
        /// </summary>
        public Action? AlTomarlo { get; init; }

        public async Task<CaeManager.Domain.Common.Result> EjecutarAsync(
            Func<CancellationToken, Task<CaeManager.Domain.Common.Result>> operacion, CancellationToken cancellationToken = default)
        {
            Ejecuciones++;
            AlTomarlo?.Invoke();
            Dentro = true;
            try { return await operacion(cancellationToken); }
            finally { Dentro = false; }
        }
    }

    public sealed class TenantActualFijo(Guid? tenantId) : ITenantActual
    {
        public Guid? TenantId => tenantId;
    }
}