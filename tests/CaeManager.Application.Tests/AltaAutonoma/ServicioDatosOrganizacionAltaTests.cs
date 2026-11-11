using CaeManager.Application.AltaAutonoma;
using CaeManager.Domain.AltaAutonoma;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CaeManager.Application.Tests.AltaAutonoma;

public class ServicioDatosOrganizacionAltaTests
{
    private const string RazonSocial = "Andamios del Sur SL";
    private const string Cif = "B12345674";
    private const string Domicilio = "Calle Mayor 1, 41001 Sevilla";
    private const string Telefono = "600 123 456";

    [Fact]
    public async Task Un_CIF_con_Tenant_propietario_deja_un_aviso_estable_a_Soporte_TALVEG_sin_datos_personales()
    {
        var id = Guid.NewGuid();
        var (servicio, acceso, registro) = Crear(new TransicionDatosOrganizacionAlta(ResultadoDatosOrganizacionAlta.DerivadaAAcceso, id));

        var resultado = await servicio.CompletarAsync(ServicioRegistroAlta.NuevoToken(), Datos(), CancellationToken.None);

        resultado.Should().Be(ResultadoDatosOrganizacionAlta.DerivadaAAcceso);
        acceso.Completados.Should().Be(1);
        var entrada = registro.Entradas.Should().ContainSingle().Subject;
        entrada.Evento.Should().Be(ServicioDatosOrganizacionAlta.EventoDerivadaAAcceso);
        entrada.Evento.Id.Should().Be(44001, "el EventId es el contrato con quien vigila el registro");
        entrada.Nivel.Should().Be(LogLevel.Warning);
        entrada.Valores.Should().Contain(new KeyValuePair<string, object?>("SolicitudAltaId", id));
        NoContieneDatosPersonales(entrada);
    }

    [Fact]
    public async Task Un_CIF_libre_deja_DatosCompletos_y_un_registro_sin_datos_personales()
    {
        var id = Guid.NewGuid();
        var (servicio, _, registro) = Crear(new TransicionDatosOrganizacionAlta(ResultadoDatosOrganizacionAlta.DatosCompletos, id));

        var resultado = await servicio.CompletarAsync(ServicioRegistroAlta.NuevoToken(), Datos(), CancellationToken.None);

        resultado.Should().Be(ResultadoDatosOrganizacionAlta.DatosCompletos);
        var entrada = registro.Entradas.Should().ContainSingle().Subject;
        entrada.Evento.Id.Should().Be(44002);
        entrada.Valores.Should().Contain(new KeyValuePair<string, object?>("SolicitudAltaId", id));
        NoContieneDatosPersonales(entrada);
    }

    [Fact]
    public async Task Un_credencial_que_la_base_no_acepta_no_deja_aviso()
    {
        var (servicio, _, registro) = Crear(new TransicionDatosOrganizacionAlta(ResultadoDatosOrganizacionAlta.NoValido, null));

        (await servicio.CompletarAsync(ServicioRegistroAlta.NuevoToken(), Datos(), CancellationToken.None))
            .Should().Be(ResultadoDatosOrganizacionAlta.NoValido);
        registro.Entradas.Should().BeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("corto")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA+")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public async Task Un_credencial_sin_la_forma_de_256_bits_no_llega_a_la_base(string? credencial)
    {
        var (servicio, acceso, registro) = Crear(new TransicionDatosOrganizacionAlta(ResultadoDatosOrganizacionAlta.DatosCompletos, Guid.NewGuid()));

        (await servicio.CompletarAsync(credencial, Datos(), CancellationToken.None)).Should().Be(ResultadoDatosOrganizacionAlta.NoValido);
        acceso.Completados.Should().Be(0);
        registro.Entradas.Should().BeEmpty();
    }

    private static void NoContieneDatosPersonales(Entrada entrada)
    {
        foreach (var dato in new[] { RazonSocial, Cif, Domicilio, Telefono })
        {
            entrada.Texto.Should().NotContain(dato);
            entrada.Valores.Select(v => v.Value?.ToString()).Should().NotContain(dato);
        }
    }

    private static DatosOrganizacionAlta Datos() =>
        DatosOrganizacionAlta.Crear(RazonSocial, Cif, Domicilio, TramoTrabajadoresActivos.De11A40, true, false, Telefono);

    private static (ServicioDatosOrganizacionAlta, ServicioRegistroAltaTests.AccesoFalso, Registro) Crear(TransicionDatosOrganizacionAlta transicion)
    {
        var acceso = new ServicioRegistroAltaTests.AccesoFalso { Transicion = transicion };
        var registro = new Registro();
        return (new ServicioDatosOrganizacionAlta(acceso, registro), acceso, registro);
    }

    private sealed record Entrada(LogLevel Nivel, EventId Evento, string Texto, IReadOnlyList<KeyValuePair<string, object?>> Valores);

    private sealed class Registro : ILogger<ServicioDatosOrganizacionAlta>
    {
        public List<Entrada> Entradas { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entradas.Add(new Entrada(logLevel, eventId, formatter(state, exception),
                state as IReadOnlyList<KeyValuePair<string, object?>> ?? []));
    }
}
