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
        new(new EmpresasQueryContextFalso(), new TenantActualFijo(Guid.NewGuid()), new AlertaOperativaFalsa());

    /// <summary>El límite sobre <paramref name="empresas"/>, el mismo contexto que lee el handler.</summary>
    public static LimiteEmpresaPropia Sobre(EmpresasQueryContextFalso empresas, AlertaOperativaFalsa alertas, Guid? tenantId = null) =>
        new(empresas, new TenantActualFijo(tenantId ?? Guid.NewGuid()), alertas);

    public sealed class TenantActualFijo(Guid? tenantId) : ITenantActual
    {
        public Guid? TenantId => tenantId;
    }
}