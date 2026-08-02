using CaeManager.Application.Centros;
using CaeManager.Application.Clientes;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos;
using CaeManager.Application.Empresas;
using CaeManager.Application.Subcontratas;
using CaeManager.Application.Trabajadores;
using CaeManager.Application.Vehiculos;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Clientes;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Subcontratas;
using CaeManager.Domain.Trabajadores;
using CaeManager.Domain.Vehiculos;
using FluentAssertions;
using MediatR;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// P1-4 de la revisión de madurez: hasta ahora el alcance de cartera en
/// escritura (<see cref="IAlcanceDatosService"/>) era un ítem de checklist
/// manual (<c>CODING_STANDARDS.md</c> § "Checklist de seguridad") — ya se
/// olvidó una vez con <c>EliminarCliente</c>/<c>EliminarDocumento</c>/
/// <c>EliminarTrabajador</c> (hallazgo de revisión de P3-31/P3-29, corregido)
/// y de nuevo con <c>EliminarCentro</c>/<c>EliminarEmpresa</c>/
/// <c>EliminarSubcontrata</c>/<c>EliminarVehiculo</c> (corregido junto con
/// este test). Este test convierte la regla en una comprobación de
/// arquitectura: cualquier handler que dependa de uno de los repositorios de
/// un agregado con alcance de cartera definido debe depender también de
/// <see cref="IAlcanceDatosService"/> — la comprobación en sí (que de verdad
/// se llame antes de actuar) sigue siendo responsabilidad del revisor, esto
/// solo evita que el olvido total pase desapercibido.
///
/// Deliberadamente acotado a los 7 agregados que ya tienen un método
/// "XxxVisibleAsync" (<see cref="AlcanceDatosServiceExtensions"/>/
/// <see cref="DocumentoAlcanceExtensions"/>): extender el mecanismo a otros
/// agregados (Proyecto, Evaluación, Incidencia...) es una decisión de
/// alcance de cartera todavía sin definir para ellos, no un olvido de este
/// mecanismo — no se inventa aquí.
/// </summary>
public class AlcanceDeCarteraEnEscrituraTests
{
    private static readonly Dictionary<Type, string> RepositoriosConAlcance = new()
    {
        [typeof(IClienteRepository)] = nameof(IClienteRepository),
        [typeof(ICentroRepository)] = nameof(ICentroRepository),
        [typeof(IEmpresaRepository)] = nameof(IEmpresaRepository),
        [typeof(ISubcontrataRepository)] = nameof(ISubcontrataRepository),
        [typeof(ITrabajadorRepository)] = nameof(ITrabajadorRepository),
        [typeof(IVehiculoRepository)] = nameof(IVehiculoRepository),
        [typeof(IDocumentoRepository)] = nameof(IDocumentoRepository),
    };

    [Fact]
    public void Todo_handler_de_escritura_que_carga_un_agregado_con_cartera_comprueba_IAlcanceDatosService()
    {
        var application = typeof(ICommand).Assembly;

        var handlersDeEscritura = application.GetTypes()
            .Where(t => !t.IsAbstract && !t.IsInterface)
            .SelectMany(t => t.GetInterfaces()
                .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IRequestHandler<,>))
                .Select(i => new { Handler = t, Command = i.GetGenericArguments()[0] }))
            .Where(x => typeof(ICommandBase).IsAssignableFrom(x.Command))
            // Solo Commands que operan sobre una entidad ya existente (Id de
            // tipo Guid, no Guid?) — un CrearXCommand puede depender del
            // mismo repositorio (p. ej. para comprobar duplicados) sin que
            // eso implique cargar una fila ajena por Id.
            .Where(x => x.Command.GetProperty("Id")?.PropertyType == typeof(Guid))
            .ToList();

        var infractores = new List<string>();

        foreach (var handlerInfo in handlersDeEscritura)
        {
            var parametros = handlerInfo.Handler.GetConstructors()
                .SelectMany(c => c.GetParameters())
                .Select(p => p.ParameterType)
                .ToHashSet();

            var repositorioConAlcance = parametros
                .Where(RepositoriosConAlcance.ContainsKey)
                .ToList();

            if (repositorioConAlcance.Count == 0) continue;
            if (parametros.Contains(typeof(IAlcanceDatosService))) continue;

            infractores.Add(
                $"{handlerInfo.Handler.Name} (depende de {string.Join(", ", repositorioConAlcance.Select(r => RepositoriosConAlcance[r]))})");
        }

        infractores.Should().BeEmpty(
            "un handler que carga un agregado con cartera definida (Cliente/Centro/Empresa/Subcontrata/" +
            "Trabajador/Vehiculo/Documento) por Id debe comprobar IAlcanceDatosService antes de actuar sobre él " +
            "— si esto falla, el handler listado puede estar dejando editar/borrar una fila fuera de la cartera " +
            "del usuario con solo conocer su Id (ver EliminarClienteCommand como plantilla del fix)");
    }
}
