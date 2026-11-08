using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Domain.Centros;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Documentos.Acreditacion;

/// <summary>
/// Conciliación de la regla A-1 sobre lo que ya está guardado: crea las acreditaciones de plataforma que falten
/// para los Documentos de Empresa operativos del Tenant en curso, con la misma regla que las altas
/// (<see cref="IAltaAcreditacionesPlataformaService"/>, <see cref="AltasConAcreditacion.CentrosAConciliar"/>). Cada
/// Centro con acceso de plataforma se evalúa entero: un Documento de Empresa queda acreditado ante cada acceso de
/// plataforma de cada Centro de su Empresa (o donde trabaja alguien de ella) que exige su tipo; un Centro sin gestión
/// CAE queda fuera, igual que en el alta.
///
/// <para>
/// Cubre lo que el alta no alcanza: los datos anteriores a la regla A-1, los Documentos de Empresa creados por una
/// instancia anterior durante el relevo azul/verde, y el par que dos altas simultáneas (un Documento y el acceso de
/// su Centro) dejaron sin crear porque ninguna veía lo que la otra aún no había confirmado.
/// </para>
///
/// <para>
/// Idempotente: el alta no agrega un par que ya existe, y un choque con el índice único durante el guardado se trata
/// como «ya existe». Solo agrega; nunca da de baja ni modifica una acreditación existente. No repara los Documentos
/// de Trabajador, cuya regla de alta no cambió con A-1.
/// </para>
///
/// <para>
/// Un solo Tenant cada vez: lee y escribe con los contextos del Tenant en curso, bajo la RLS normal, sin
/// <c>IgnoreQueryFilters</c> ni identidad que se salte la RLS. Quien la invoca abre el ámbito de cada Tenant
/// propietario (<c>ConciliacionAcreditacionesEmpresaHostedService</c>). No se ejecuta sola: el disparador está
/// apagado por defecto y lo enciende quien despliega.
/// </para>
/// </summary>
public interface IConciliacionAcreditacionesEmpresaService
{
    /// <returns>Número de acreditaciones creadas en el Tenant en curso.</returns>
    Task<int> ConciliarTenantEnCursoAsync(CancellationToken cancellationToken = default);
}

public class ConciliacionAcreditacionesEmpresaService(
    ICentrosQueryContext centrosContext,
    IAltaAcreditacionesPlataformaService altaAcreditaciones,
    IUnitOfWork unitOfWork)
    : IConciliacionAcreditacionesEmpresaService
{
    /// <summary>
    /// Centros por guardado: acota la memoria del rastreo y el tamaño de las listas de la consulta en un Tenant con
    /// muchos Centros. Cada lote se guarda entero o nada; uno ya guardado no se repite en la pasada siguiente.
    /// </summary>
    internal const int CentrosPorLote = 100;

    public async Task<int> ConciliarTenantEnCursoAsync(CancellationToken cancellationToken = default)
    {
        // Solo el identificador del Centro: el acceso guarda credenciales cifradas que aquí no hacen falta.
        var centrosConPlataforma = await centrosContext.CanalesGestionDocumental
            .Where(c => c.Tipo == TipoCanalGestion.Plataforma)
            .Select(c => c.CentroId)
            .Distinct()
            .OrderBy(id => id)
            .ToListAsync(cancellationToken);

        var creadas = 0;
        foreach (var lote in centrosConPlataforma.Chunk(CentrosPorLote))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var agregadas = await altaAcreditaciones.AgregarPendientesAsync(
                new AltasConAcreditacion { CentrosAConciliar = lote, SoloDocumentosDeEmpresa = true }, cancellationToken);
            if (agregadas == 0)
                continue;

            await unitOfWork.SaveChangesAsync(cancellationToken);
            creadas += agregadas;
        }

        return creadas;
    }
}
