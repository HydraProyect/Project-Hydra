using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Empresas;
using CaeManager.Application.Trabajadores;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Trabajadores;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Importacion.Commands.EjecutarImportacionCombinada;

/// <summary>
/// Escribe en la base de datos un plan ya analizado por
/// AnalizarPlantillaCombinadaQuery. <see cref="ReemplazarExistentes"/>
/// decide qué pasa con una fila que coincide con un registro que ya
/// existía: <c>true</c> sobrescribe sus campos con los del archivo (y, para
/// Empresa↔Cliente, reemplaza la lista de asociaciones exactamente como
/// venga en el archivo); <c>false</c> (por defecto, el modo seguro) solo
/// rellena lo que esté vacío en el registro existente y solo AÑADE
/// asociaciones nuevas, nunca quita ni sobrescribe un dato ya presente.
/// </summary>
public record EjecutarImportacionCombinadaCommand(PlanImportacionCombinadaDto Plan, bool ReemplazarExistentes)
    : ICommand<ResultadoImportacionCombinadaDto>;

public record ResultadoImportacionCombinadaDto(
    int ClientesCreados,
    int ClientesActualizados,
    int EmpresasCreadas,
    int EmpresasActualizadas,
    int CentrosCreados,
    int CentrosActualizados,
    int TrabajadoresCreados,
    int TrabajadoresActualizados,
    IReadOnlyList<ItemImportacionDto> Advertencias,
    IReadOnlyList<ItemImportacionDto> Omitidos);

/// <summary>
/// F3b — "Clientes" y "Empresas" del archivo escriben ahora en el mismo
/// agregado (<see cref="Empresa"/>): ya no hace falta <c>IClienteRepository</c>
/// ni <c>IClientesQueryContext</c>, solo <c>IEmpresaRepository</c>/
/// <c>IEmpresasQueryContext</c> para ambas hojas.
///
/// Cuidado de correctitud que la unificación introduce y que no existía
/// antes: <c>empresasPorRazonSocial</c> (usado por la hoja "Empresas") se
/// siembra ANTES de la consulta a partir de las filas de la hoja "Clientes"
/// ya procesadas — si no, una fila "Empresas" con la misma razón social que
/// un Cliente recién creado en esta misma ejecución no lo encontraría
/// todavía en la base de datos (la fila del Cliente está en el
/// change tracker, sin guardar) e intentaría crear una segunda Empresa con
/// la misma razón social, chocando con el índice único global en el
/// <c>SaveChangesAsync</c> final. Antes de F3 esto no podía pasar —
/// Cliente y Empresa eran tablas independientes sin unicidad cruzada.
/// </summary>
public class EjecutarImportacionCombinadaCommandHandler(
    IEmpresaRepository empresaRepositorio,
    IEmpresaClienteRepository empresaClienteRepositorio,
    ICentroRepository centroRepositorio,
    ITrabajadorRepository trabajadorRepositorio,
    ICentrosQueryContext centrosContext, IEmpresasQueryContext empresasContext, ITrabajadoresQueryContext trabajadoresContext,
    IUnitOfWork unitOfWork)
    : IRequestHandler<EjecutarImportacionCombinadaCommand, Result<ResultadoImportacionCombinadaDto>>
{
    public async Task<Result<ResultadoImportacionCombinadaDto>> Handle(
        EjecutarImportacionCombinadaCommand request, CancellationToken cancellationToken)
    {
        var plan = request.Plan;
        var reemplazar = request.ReemplazarExistentes;
        var omitidosEnEscritura = new List<ItemImportacionDto>();

        var clientesPorCif = await empresasContext.Empresas
            .Where(e => e.Cif != null)
            .ToDictionaryAsync(e => e.Cif!, StringComparer.OrdinalIgnoreCase, cancellationToken);

        // Indexado por razón social para que Empresas/Centros/Trabajadores puedan
        // referenciar un Cliente por nombre. Incluye tanto el nombre real que
        // tiene en base de datos como el nombre indicado en el archivo — en modo
        // fusionar, un Cliente existente no se renombra, así que si el archivo
        // usa un nombre distinto al que ya tenía, ambos deben resolver al mismo
        // Cliente (si solo indexáramos el nombre "actual" tras la fusión, una
        // fila de Empresas/Centros que usa el nombre del archivo dejaría de
        // encontrarlo).
        var clientesIdPorRazonSocial = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        foreach (var cliente in clientesPorCif.Values)
            clientesIdPorRazonSocial[cliente.RazonSocial] = cliente.Id;

        var clientesCreados = 0;
        var clientesActualizados = 0;

        foreach (var fila in plan.Clientes)
        {
            if (clientesPorCif.TryGetValue(fila.Cif, out var existente))
            {
                if (reemplazar && (existente.RazonSocial != fila.RazonSocial || existente.EsCritico != fila.EsCritico))
                {
                    existente.ActualizarComoCliente(fila.RazonSocial, fila.Cif, fila.EsCritico, existente.Notas);
                    clientesActualizados++;
                }

                clientesIdPorRazonSocial[fila.RazonSocial] = existente.Id;
                continue;
            }

            try
            {
                var cliente = Empresa.CrearComoCliente(fila.RazonSocial, fila.Cif, fila.EsCritico, notas: null, ejecutivoUsuarioId: null);
                empresaRepositorio.Agregar(cliente);
                clientesPorCif[fila.Cif] = cliente;
                clientesIdPorRazonSocial[fila.RazonSocial] = cliente.Id;
                clientesCreados++;
            }
            catch (ArgumentException ex)
            {
                omitidosEnEscritura.Add(new ItemImportacionDto("Clientes", 0, fila.RazonSocial, ex.Message));
            }
        }

        // Semilla con lo que la hoja "Clientes" ya creó/tocó en memoria (ver
        // comentario de clase) antes de consultar la base de datos — luego la
        // consulta a la base de datos completa el resto sin pisar estas.
        var empresasPorRazonSocial = new Dictionary<string, Empresa>(StringComparer.OrdinalIgnoreCase);
        foreach (var cliente in clientesPorCif.Values)
            empresasPorRazonSocial[cliente.RazonSocial] = cliente;
        foreach (var empresa in await empresasContext.Empresas.ToListAsync(cancellationToken))
            empresasPorRazonSocial.TryAdd(empresa.RazonSocial, empresa);

        var asociacionesActuales = await empresasContext.EmpresasClientes.ToListAsync(cancellationToken);

        var empresasCreadas = 0;
        var empresasActualizadas = 0;

        foreach (var fila in plan.Empresas)
        {
            Empresa empresa;
            var esNueva = !empresasPorRazonSocial.TryGetValue(fila.RazonSocial, out var existente);

            if (esNueva)
            {
                try
                {
                    empresa = new Empresa(fila.RazonSocial);
                    empresaRepositorio.Agregar(empresa);
                    empresasPorRazonSocial[fila.RazonSocial] = empresa;
                    empresasCreadas++;
                }
                catch (ArgumentException ex)
                {
                    omitidosEnEscritura.Add(new ItemImportacionDto("Empresas", 0, fila.RazonSocial, ex.Message));
                    continue;
                }
            }
            else
            {
                empresa = existente!;
            }

            var clienteIdsDeseados = fila.ClientesAsociados
                .Where(nombre => clientesIdPorRazonSocial.ContainsKey(nombre))
                .Select(nombre => clientesIdPorRazonSocial[nombre])
                .ToHashSet();

            var asociacionesDeLaEmpresa = asociacionesActuales.Where(ec => ec.EmpresaId == empresa.Id).ToList();
            var clienteIdsActuales = asociacionesDeLaEmpresa.Select(ec => ec.ClienteId).ToHashSet();

            if (reemplazar)
            {
                foreach (var ec in asociacionesDeLaEmpresa.Where(ec => !clienteIdsDeseados.Contains(ec.ClienteId)))
                {
                    empresaClienteRepositorio.Eliminar(ec);
                    asociacionesActuales.Remove(ec);
                }
            }

            var nuevasAsociaciones = 0;
            foreach (var clienteId in clienteIdsDeseados.Except(clienteIdsActuales))
            {
                var nuevaAsociacion = new EmpresaCliente(empresa.Id, clienteId);
                empresaClienteRepositorio.Agregar(nuevaAsociacion);
                asociacionesActuales.Add(nuevaAsociacion);
                nuevasAsociaciones++;
            }

            if (!esNueva && (nuevasAsociaciones > 0 || (reemplazar && asociacionesDeLaEmpresa.Count != clienteIdsDeseados.Count)))
                empresasActualizadas++;
        }

        var empresasIdPorRazonSocial = empresasPorRazonSocial.Values.ToDictionary(e => e.RazonSocial, e => e.Id, StringComparer.OrdinalIgnoreCase);

        var centrosExistentes = await centrosContext.Centros.ToListAsync(cancellationToken);
        var centrosPorClienteYNombre = centrosExistentes.ToDictionary(
            c => (c.ClienteId, c.Nombre.ToUpperInvariant()), c => c);

        var centrosCreados = 0;
        var centrosActualizados = 0;

        foreach (var fila in plan.Centros)
        {
            if (!clientesIdPorRazonSocial.TryGetValue(fila.RazonSocialCliente, out var clienteId))
            {
                omitidosEnEscritura.Add(new ItemImportacionDto(
                    "Centros", 0, fila.Nombre, $"No se encontró el cliente \"{fila.RazonSocialCliente}\" (ni en el sistema ni en la hoja Clientes de este archivo)."));
                continue;
            }

            if (!empresasIdPorRazonSocial.TryGetValue(fila.RazonSocialEmpresa, out var empresaId))
            {
                omitidosEnEscritura.Add(new ItemImportacionDto(
                    "Centros", 0, fila.Nombre, $"No se encontró la empresa \"{fila.RazonSocialEmpresa}\" (ni en el sistema ni en la hoja Empresas de este archivo)."));
                continue;
            }

            var clave = (clienteId, fila.Nombre.ToUpperInvariant());

            if (centrosPorClienteYNombre.TryGetValue(clave, out var existente))
            {
                var codigoCentro = ResolverTexto(existente.CodigoCentro, fila.CodigoCentro, reemplazar);
                var direccion = ResolverTexto(existente.Direccion, fila.Direccion, reemplazar);
                var contacto = ResolverTexto(existente.Contacto, fila.Contacto, reemplazar);
                var contratoVigenteHasta = reemplazar
                    ? fila.ContratoVigenteHasta ?? existente.ContratoVigenteHasta
                    : existente.ContratoVigenteHasta ?? fila.ContratoVigenteHasta;

                if (codigoCentro != existente.CodigoCentro || direccion != existente.Direccion
                    || contacto != existente.Contacto || contratoVigenteHasta != existente.ContratoVigenteHasta)
                {
                    existente.Actualizar(existente.Nombre, codigoCentro, direccion, contacto, contratoVigenteHasta);
                    centrosActualizados++;
                }

                continue;
            }

            try
            {
                var centro = new Centro(
                    clienteId, empresaId, fila.Nombre, fila.CodigoCentro, fila.Direccion, fila.Contacto, fila.ContratoVigenteHasta);
                centroRepositorio.Agregar(centro);
                centrosPorClienteYNombre[clave] = centro;
                centrosCreados++;
            }
            catch (ArgumentException ex)
            {
                omitidosEnEscritura.Add(new ItemImportacionDto("Centros", 0, fila.Nombre, ex.Message));
            }
        }

        var trabajadoresExistentes = await trabajadoresContext.Trabajadores.ToDictionaryAsync(
            t => t.Dni, StringComparer.OrdinalIgnoreCase, cancellationToken);

        var trabajadoresCreados = 0;
        var trabajadoresActualizados = 0;

        foreach (var fila in plan.Trabajadores)
        {
            if (!empresasIdPorRazonSocial.TryGetValue(fila.RazonSocialEmpresa, out var empresaId))
            {
                omitidosEnEscritura.Add(new ItemImportacionDto(
                    "Trabajadores", 0, $"{fila.Nombre} {fila.Apellidos} ({fila.Dni})",
                    $"No se encontró la empresa \"{fila.RazonSocialEmpresa}\" (ni en el sistema ni en la hoja Empresas de este archivo)."));
                continue;
            }

            if (trabajadoresExistentes.TryGetValue(fila.Dni, out var existente))
            {
                var fechaNacimiento = reemplazar
                    ? fila.FechaNacimiento ?? existente.FechaNacimiento
                    : existente.FechaNacimiento ?? fila.FechaNacimiento;
                var email = ResolverTexto(existente.Email, fila.Email, reemplazar);

                if (fechaNacimiento != existente.FechaNacimiento || email != existente.Email)
                {
                    existente.Actualizar(existente.Nombre, existente.Apellidos, fechaNacimiento, email, existente.Observaciones, existente.Alias);
                    trabajadoresActualizados++;
                }

                continue;
            }

            try
            {
                var trabajador = Trabajador.DeEmpresa(empresaId, fila.Nombre, fila.Apellidos, fila.Dni, fila.FechaNacimiento, fila.Email);
                trabajadorRepositorio.Agregar(trabajador);
                trabajadoresExistentes[fila.Dni] = trabajador;
                trabajadoresCreados++;
            }
            catch (ArgumentException ex)
            {
                omitidosEnEscritura.Add(new ItemImportacionDto(
                    "Trabajadores", 0, $"{fila.Nombre} {fila.Apellidos} ({fila.Dni})", ex.Message));
            }
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return new ResultadoImportacionCombinadaDto(
            clientesCreados, clientesActualizados, empresasCreadas, empresasActualizadas,
            centrosCreados, centrosActualizados, trabajadoresCreados, trabajadoresActualizados,
            plan.Advertencias, [.. plan.Omitidos, .. omitidosEnEscritura]);
    }

    /// <summary>
    /// Con Reemplazar=true, el valor del archivo gana siempre que venga
    /// informado; con Reemplazar=false (fusionar), el valor existente solo
    /// se completa si estaba vacío — nunca se sobrescribe un dato ya presente.
    /// </summary>
    private static string? ResolverTexto(string? actual, string? nuevo, bool reemplazar) =>
        reemplazar
            ? (string.IsNullOrWhiteSpace(nuevo) ? actual : nuevo)
            : (string.IsNullOrWhiteSpace(actual) ? nuevo : actual);
}
