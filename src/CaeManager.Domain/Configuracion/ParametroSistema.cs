using CaeManager.Domain.Common;

namespace CaeManager.Domain.Configuracion;

/// <summary>
/// Configuración global editable por Administrador. Fila única en la base de
/// datos (impuesto por Infrastructure, no por esta clase). Corresponde a la
/// sección "Umbrales de alerta" de la hoja "Parametros" del Excel original.
/// </summary>
public class ParametroSistema : Entity
{
    public int UmbralAmbarDias { get; private set; }
    public int UmbralRojoDias { get; private set; }

    private ParametroSistema()
    {
    }

    public ParametroSistema(int umbralAmbarDias, int umbralRojoDias)
    {
        Actualizar(umbralAmbarDias, umbralRojoDias);
    }

    public void Actualizar(int umbralAmbarDias, int umbralRojoDias)
    {
        if (umbralRojoDias <= 0)
            throw new ArgumentException("El umbral rojo debe ser mayor que cero.", nameof(umbralRojoDias));
        if (umbralAmbarDias <= umbralRojoDias)
            throw new ArgumentException("El umbral ámbar debe ser mayor que el umbral rojo.", nameof(umbralAmbarDias));

        UmbralAmbarDias = umbralAmbarDias;
        UmbralRojoDias = umbralRojoDias;
    }
}
