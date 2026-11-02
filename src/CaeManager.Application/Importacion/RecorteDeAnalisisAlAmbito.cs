using CaeManager.Application.Asignaciones;
using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Empresas;
using CaeManager.Application.Trabajadores;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Importacion;

/// <summary>
/// Recorta al ámbito del acto la vista previa de una carga masiva. Los analizadores marcan <c>YaExiste</c> contra el Tenant
/// entero; con autoridad <see cref="AutoridadCargaMasiva.Acotada"/> eso sería un oráculo de existencia («este DNI, este CIF,
/// este Centro ya existen») sobre datos que el Coordinador CAE no puede ver. Aquí una fila solo conserva <c>YaExiste</c> si
/// lo existente está dentro de su ámbito; lo de fuera se presenta como nuevo, igual que lo que no existe, y la ejecución lo
/// omite y lo cuenta con el motivo uniforme (<see cref="AmbitoCargaMasiva.Motivo"/>). La ejecución sigue comprobando la
/// existencia de forma autoritativa; esto solo afecta a lo que se enseña. Con autoridad Total no recorta nada.
/// </summary>
public sealed class RecorteDeAnalisisAlAmbito(
    IAutorizacionEscrituraEfectiva autorizacion,
    IAlcanceDatosService alcance,
    IEmpresasQueryContext empresasContext,
    ICentrosQueryContext centrosContext,
    ITrabajadoresQueryContext trabajadoresContext,
    IAsignacionesQueryContext asignacionesContext)
{
    private sealed record Existentes(
        HashSet<string> Clientes, HashSet<string> Empresas, HashSet<string> Centros, HashSet<string> Cifs, HashSet<string> Dnis,
        HashSet<(string Dni, string Centro)> Asignaciones);

    public async Task<PlanImportacionDto> RecortarAsync(PlanImportacionDto plan, CancellationToken cancellationToken)
    {
        var ambito = await ResolverAsync(cancellationToken);
        if (ambito is null) return plan;
        var e = await CargarAsync(ambito, cancellationToken);

        // Los analizadores de las plantillas de Clientes y de Documentos solo emiten la fila si el Cliente, el Centro o el
        // Trabajador existen en el Tenant, y omiten «no existe» si no: dejar la fila a quien no ve lo existente sería el mismo
        // oráculo. Fuera del ámbito se convierte en el mismo omitido que la inexistencia (mismo texto, misma fila).
        var omitidos = new List<ItemImportacionDto>(plan.Omitidos);
        var clientesCentros = plan.ClientesCentros;
        var documentos = plan.Documentos;
        if (plan.Empresas.Count == 0 && plan.Trabajadores.Count == 0 && plan.Documentos.Count == 0)
        {
            var visibles = new List<ClienteCentroImportadoDto>();
            foreach (var c in plan.ClientesCentros)
            {
                if (!e.Clientes.Contains(c.Nombre))
                    omitidos.Add(new ItemImportacionDto(c.Hoja, c.Fila, c.Nombre,
                        "Este Cliente no existe todavía. Ahora requiere una identificación fiscal (CIF, DNI o NIE), que esta plantilla no recoge — créalo manualmente en Clientes."));
                else if (!e.Centros.Contains(c.Nombre))
                    omitidos.Add(new ItemImportacionDto(c.Hoja, c.Fila, c.Nombre,
                        "Este centro no existe todavía. Ahora requiere una Empresa asociada, que esta plantilla no recoge — créalo manualmente en Centros."));
                else
                    visibles.Add(c);
            }
            clientesCentros = visibles;
        }
        else if (plan.Trabajadores.Count == 0 && plan.ClientesCentros.Count == 0 && plan.Empresas.Count == 0)
        {
            var visibles = new List<DocumentoImportadoDto>();
            foreach (var d in plan.Documentos)
            {
                if (e.Dnis.Contains(d.Dni))
                    visibles.Add(d);
                else
                    omitidos.Add(new ItemImportacionDto(d.Hoja, d.Fila, d.Dni,
                        "No existe ningún trabajador con este DNI. Da de alta al trabajador antes de importar sus documentos."));
            }
            documentos = visibles;
        }
        omitidos = [.. omitidos.OrderBy(o => o.Fila)];

        return plan with
        {
            ClientesCentros = [.. clientesCentros.Select(c => c with
            {
                YaExisteCliente = c.YaExisteCliente && e.Clientes.Contains(c.Nombre),
                YaExisteCentro = c.YaExisteCentro && e.Centros.Contains(c.Nombre),
            })],
            Empresas = [.. plan.Empresas.Select(x => x with { YaExiste = x.YaExiste && e.Empresas.Contains(x.RazonSocial) })],
            Trabajadores = [.. plan.Trabajadores.Select(t => t with { YaExiste = t.YaExiste && e.Dnis.Contains(t.Dni) })],
            Documentos = [.. documentos.Select(d => d with { YaExiste = d.YaExiste && e.Dnis.Contains(d.Dni) })],
            Asignaciones = [.. plan.Asignaciones.Select(a => a with
            {
                YaExiste = a.YaExiste && e.Dnis.Contains(a.Dni) && e.Centros.Contains(a.NombreCentro)
                    && e.Asignaciones.Contains((a.Dni, a.NombreCentro)),
            })],
            Omitidos = omitidos,
        };
    }

    public async Task<PlanImportacionCombinadaDto> RecortarAsync(PlanImportacionCombinadaDto plan, CancellationToken cancellationToken)
    {
        var ambito = await ResolverAsync(cancellationToken);
        if (ambito is null) return plan;
        var e = await CargarAsync(ambito, cancellationToken);

        return plan with
        {
            Clientes = [.. plan.Clientes.Select(c => c with { YaExiste = c.YaExiste && e.Cifs.Contains(c.Cif.Trim()) })],
            Empresas = [.. plan.Empresas.Select(x => x with { YaExiste = x.YaExiste && e.Empresas.Contains(x.RazonSocial) })],
            Centros = [.. plan.Centros.Select(c => c with
            {
                YaExiste = c.YaExiste && e.Centros.Contains(c.Nombre),
            })],
            Trabajadores = [.. plan.Trabajadores.Select(t => t with { YaExiste = t.YaExiste && e.Dnis.Contains(t.Dni) })],
        };
    }

    private async Task<AmbitoCargaMasiva?> ResolverAsync(CancellationToken cancellationToken)
    {
        var autoridad = await autorizacion.ObtenerAutoridadCargaMasivaAsync(cancellationToken);
        // Fallo cerrado: sin autoridad el ámbito es vacío y nada «ya existe»; Total no recorta.
        return autoridad == AutoridadCargaMasiva.Total
            ? null
            : await AmbitoCargaMasiva.ResolverAsync(autoridad, alcance, cancellationToken);
    }

    private async Task<Existentes> CargarAsync(AmbitoCargaMasiva ambito, CancellationToken cancellationToken)
    {
        var comparador = StringComparer.OrdinalIgnoreCase;

        var empresas = await empresasContext.Empresas
            .Select(x => new { x.Id, x.RazonSocial, x.Cif })
            .ToListAsync(cancellationToken);
        var clientesEnAmbito = empresas.Where(x => ambito.Cliente(x.Id)).ToList();

        var centros = await centrosContext.Centros
            .Select(c => new { c.Id, c.Nombre })
            .ToListAsync(cancellationToken);
        var centrosEnAmbito = centros.Where(c => ambito.Centro(c.Id)).ToList();

        var trabajadores = await trabajadoresContext.Trabajadores
            .Where(t => t.Dni != null)
            .Select(t => new { t.Id, Dni = t.Dni! })
            .ToListAsync(cancellationToken);
        var trabajadoresEnAmbito = trabajadores.Where(t => ambito.Trabajador(t.Id)).ToList();
        var dniPorId = trabajadoresEnAmbito.ToDictionary(t => t.Id, t => t.Dni);

        var centroPorId = centrosEnAmbito.ToDictionary(c => c.Id, c => c.Nombre);
        var asignaciones = await asignacionesContext.Asignaciones
            .Where(a => a.FechaBaja == null)
            .Select(a => new { a.TrabajadorId, a.CentroId })
            .ToListAsync(cancellationToken);

        return new Existentes(
            new HashSet<string>(clientesEnAmbito.Select(x => x.RazonSocial), comparador),
            new HashSet<string>(empresas.Where(x => ambito.Empresa(x.Id)).Select(x => x.RazonSocial), comparador),
            new HashSet<string>(centrosEnAmbito.Select(c => c.Nombre), comparador),
            new HashSet<string>(clientesEnAmbito.Where(x => x.Cif is not null).Select(x => x.Cif!.Trim()), comparador),
            new HashSet<string>(trabajadoresEnAmbito.Select(t => t.Dni), comparador),
            [.. asignaciones
                .Where(a => dniPorId.ContainsKey(a.TrabajadorId) && centroPorId.ContainsKey(a.CentroId))
                .Select(a => (dniPorId[a.TrabajadorId], centroPorId[a.CentroId]))]);
    }
}
