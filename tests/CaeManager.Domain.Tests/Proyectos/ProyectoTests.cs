using CaeManager.Domain.Proyectos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Proyectos;

public class ProyectoTests
{
    private static readonly Guid ClienteIdValido = Guid.NewGuid();
    private static readonly Guid CentroIdValido = Guid.NewGuid();

    [Fact]
    public void Crea_un_proyecto_valido_indefinido()
    {
        var inicio = new DateOnly(2026, 8, 1);

        var proyecto = Proyecto.Crear(ClienteIdValido, CentroIdValido, "Ampliación Planta Norte", inicio, null, "Obra civil");

        proyecto.ClienteId.Should().Be(ClienteIdValido);
        proyecto.CentroId.Should().Be(CentroIdValido);
        proyecto.Nombre.Should().Be("Ampliación Planta Norte");
        proyecto.FechaInicio.Should().Be(inicio);
        proyecto.FechaFinPrevista.Should().BeNull();
        proyecto.FechaCierreReal.Should().BeNull();
        proyecto.Notas.Should().Be("Obra civil");
        proyecto.EstaAbierto.Should().BeTrue();
    }

    [Fact]
    public void Rechaza_un_cliente_vacio()
    {
        var accion = () => Proyecto.Crear(Guid.Empty, CentroIdValido, "Obra", new DateOnly(2026, 8, 1), null, null);

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Rechaza_un_centro_vacio()
    {
        var accion = () => Proyecto.Crear(ClienteIdValido, Guid.Empty, "Obra", new DateOnly(2026, 8, 1), null, null);

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Rechaza_fecha_fin_prevista_anterior_a_fecha_inicio()
    {
        var accion = () => Proyecto.Crear(
            ClienteIdValido, CentroIdValido, "Obra", new DateOnly(2026, 8, 10), new DateOnly(2026, 8, 1), null);

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Rechaza_nombre_vacio()
    {
        var accion = () => Proyecto.Crear(ClienteIdValido, CentroIdValido, "  ", new DateOnly(2026, 8, 1), null, null);

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Rechaza_nombre_demasiado_largo()
    {
        var nombreLargo = new string('a', Proyecto.LongitudMaximaNombre + 1);

        var accion = () => Proyecto.Crear(ClienteIdValido, CentroIdValido, nombreLargo, new DateOnly(2026, 8, 1), null, null);

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Actualizar_cambia_nombre_fecha_fin_y_notas()
    {
        var proyecto = Proyecto.Crear(ClienteIdValido, CentroIdValido, "Obra original", new DateOnly(2026, 8, 1), null, null);

        proyecto.Actualizar("Obra renombrada", new DateOnly(2026, 12, 1), "Notas nuevas");

        proyecto.Nombre.Should().Be("Obra renombrada");
        proyecto.FechaFinPrevista.Should().Be(new DateOnly(2026, 12, 1));
        proyecto.Notas.Should().Be("Notas nuevas");
    }

    [Fact]
    public void Cerrar_marca_el_proyecto_como_no_abierto()
    {
        var proyecto = Proyecto.Crear(ClienteIdValido, CentroIdValido, "Obra", new DateOnly(2026, 8, 1), null, null);

        proyecto.Cerrar(new DateOnly(2026, 9, 1));

        proyecto.EstaAbierto.Should().BeFalse();
        proyecto.FechaCierreReal.Should().Be(new DateOnly(2026, 9, 1));
    }

    [Fact]
    public void Cerrar_rechaza_fecha_anterior_al_inicio()
    {
        var proyecto = Proyecto.Crear(ClienteIdValido, CentroIdValido, "Obra", new DateOnly(2026, 8, 10), null, null);

        var accion = () => proyecto.Cerrar(new DateOnly(2026, 8, 1));

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Cerrar_dos_veces_lanza_excepcion()
    {
        var proyecto = Proyecto.Crear(ClienteIdValido, CentroIdValido, "Obra", new DateOnly(2026, 8, 1), null, null);
        proyecto.Cerrar(new DateOnly(2026, 9, 1));

        var accion = () => proyecto.Cerrar(new DateOnly(2026, 9, 2));

        accion.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Reabrir_permite_volver_a_cerrar()
    {
        var proyecto = Proyecto.Crear(ClienteIdValido, CentroIdValido, "Obra", new DateOnly(2026, 8, 1), null, null);
        proyecto.Cerrar(new DateOnly(2026, 9, 1));

        proyecto.Reabrir();

        proyecto.EstaAbierto.Should().BeTrue();
        proyecto.FechaCierreReal.Should().BeNull();
    }
}
