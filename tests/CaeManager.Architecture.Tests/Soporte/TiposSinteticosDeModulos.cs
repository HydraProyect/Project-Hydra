// Tipos sintéticos para los controles positivos de ModulosRegistradosTests. Viven en el ensamblado de tests, nunca en
// src, y llevan namespaces que imitan los de src para que el registro de módulos los clasifique como clasificaría un
// tipo real: un área sin registrar, una configuración EF de cada clase y un tipo fuera de CaeManager.*.
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Facturacion;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Domain.AreaSinRegistrarDeControl
{
    internal sealed class TipoSinModuloDeControl;

    internal sealed class OtroTipoDeControl
    {
        internal sealed class Anidado;
    }
}

namespace CaeManager.Infrastructure.Persistence.Configurations
{
    internal sealed class ConfiguracionDeControl : IEntityTypeConfiguration<TarifaCliente>
    {
        public void Configure(EntityTypeBuilder<TarifaCliente> builder)
        {
        }
    }

    internal sealed class ConfiguracionMixtaDeControl : IEntityTypeConfiguration<TarifaCliente>, IEntityTypeConfiguration<Empresa>
    {
        public void Configure(EntityTypeBuilder<TarifaCliente> builder)
        {
        }

        public void Configure(EntityTypeBuilder<Empresa> builder)
        {
        }
    }

    internal static class AuxiliarSinEntidadDeControl
    {
    }
}

namespace Sintetico.FueraDeTalveg
{
    internal sealed class TipoAjenoDeControl;
}
