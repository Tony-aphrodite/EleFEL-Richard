using System.Text;
using System.Xml.Linq;
using EleFEL.Core.Models;

namespace EleFEL.Core.Services;

/// <summary>
/// Generates XML DTE documents following Guatemala SAT's FEL schema.
/// Supports both Régimen General (FACT) and Pequeño Contribuyente (FPEQ).
/// </summary>
public class XmlDteGenerator
{
    private static readonly XNamespace DteNs = "http://www.sat.gob.gt/dte/fel/0.2.0";
    private static readonly XNamespace DsNs = "http://www.w3.org/2000/09/xmldsig#";
    private static readonly XNamespace XsiNs = "http://www.w3.org/2001/XMLSchema-instance";

    private readonly EmitterConfig _emitter;

    public XmlDteGenerator(EmitterConfig emitter)
    {
        _emitter = emitter;
    }

    /// <summary>
    /// Generates the complete XML DTE for a sale
    /// </summary>
    public string GenerateXml(EleventaSale sale, Customer customer)
    {
        var dteType = _emitter.TaxpayerType == "PEQ" ? "FPEQ" : "FACT";
        var now = DateTime.Now;

        var doc = new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            CreateGTDocumento(sale, customer, dteType, now)
        );

        using var ms = new MemoryStream();
        using (var xw = System.Xml.XmlWriter.Create(ms, new System.Xml.XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false),
            Indent = true
        }))
        {
            doc.Save(xw);
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private XElement CreateGTDocumento(EleventaSale sale, Customer customer, string dteType, DateTime now)
    {
        return new XElement(DteNs + "GTDocumento",
            new XAttribute("Version", "0.1"),
            new XAttribute(XNamespace.Xmlns + "dte", DteNs),
            new XAttribute(XNamespace.Xmlns + "ds", DsNs),
            new XAttribute(XNamespace.Xmlns + "xsi", XsiNs),
            new XAttribute(XsiNs + "schemaLocation", "http://www.sat.gob.gt/dte/fel/0.2.0"),
            CreateSAT(sale, customer, dteType, now)
        );
    }

    private XElement CreateSAT(EleventaSale sale, Customer customer, string dteType, DateTime now)
    {
        return new XElement(DteNs + "SAT",
            new XAttribute("ClaseDocumento", "dte"),
            CreateDTE(sale, customer, dteType, now)
        );
    }

    private XElement CreateDTE(EleventaSale sale, Customer customer, string dteType, DateTime now)
    {
        return new XElement(DteNs + "DTE",
            new XAttribute("ID", "DatosCertificados"),
            CreateDatosEmision(sale, customer, dteType, now)
        );
    }

    private XElement CreateDatosEmision(EleventaSale sale, Customer customer, string dteType, DateTime now)
    {
        var datosEmision = new XElement(DteNs + "DatosEmision",
            new XAttribute("ID", "DatosEmision"),
            CreateDatosGenerales(dteType, now),
            CreateEmisor(),
            CreateReceptor(customer),
            CreateFrases(dteType),
            CreateItems(sale, dteType),
            CreateTotales(sale, dteType)
        );

        return datosEmision;
    }

    private XElement CreateDatosGenerales(string dteType, DateTime now)
    {
        // Guatemala timezone is always UTC-06:00 per SAT FEL requirements
        var guatemalaTime = TimeZoneInfo.ConvertTimeBySystemTimeZoneId(now, "Central America Standard Time");
        var fechaEmision = guatemalaTime.ToString("yyyy-MM-ddTHH:mm:ss") + "-06:00";

        return new XElement(DteNs + "DatosGenerales",
            new XAttribute("CodigoMoneda", _emitter.CurrencyCode),
            new XAttribute("FechaHoraEmision", fechaEmision),
            new XAttribute("Tipo", dteType)
        );
    }

    private XElement CreateEmisor()
    {
        return new XElement(DteNs + "Emisor",
            new XAttribute("AfiliacionIVA", _emitter.TaxpayerType == "PEQ" ? "PEQ" : "GEN"),
            new XAttribute("CodigoEstablecimiento", _emitter.EstablishmentCode),
            new XAttribute("CorreoEmisor", ""),
            new XAttribute("NITEmisor", _emitter.Nit),
            new XAttribute("NombreComercial", _emitter.CommercialName),
            new XAttribute("NombreEmisor", _emitter.Name),
            new XElement(DteNs + "DireccionEmisor",
                new XElement(DteNs + "Direccion", _emitter.Address),
                new XElement(DteNs + "CodigoPostal", _emitter.ZipCode),
                new XElement(DteNs + "Municipio", _emitter.Municipality),
                new XElement(DteNs + "Departamento", _emitter.Department),
                new XElement(DteNs + "Pais", _emitter.Country)
            )
        );
    }

    private XElement CreateReceptor(Customer customer)
    {
        return new XElement(DteNs + "Receptor",
            new XAttribute("CorreoReceptor", ""),
            new XAttribute("IDReceptor", customer.Nit),
            new XAttribute("NombreReceptor", customer.Name),
            new XElement(DteNs + "DireccionReceptor",
                new XElement(DteNs + "Direccion", customer.Address),
                new XElement(DteNs + "CodigoPostal", "01001"),
                new XElement(DteNs + "Municipio", "Guatemala"),
                new XElement(DteNs + "Departamento", "Guatemala"),
                new XElement(DteNs + "Pais", "GT")
            )
        );
    }

    private XElement CreateFrases(string dteType)
    {
        var frases = new XElement(DteNs + "Frases");

        // Use frases from config if available
        if (_emitter.Frases.Count > 0)
        {
            foreach (var frase in _emitter.Frases)
            {
                frases.Add(new XElement(DteNs + "Frase",
                    new XAttribute("TipoFrase", frase.TipoFrase.ToString()),
                    new XAttribute("CodigoEscenario", frase.CodigoEscenario.ToString())
                ));
            }
        }
        else if (dteType == "FPEQ")
        {
            // Pequeño Contribuyente
            frases.Add(new XElement(DteNs + "Frase",
                new XAttribute("TipoFrase", "1"),
                new XAttribute("CodigoEscenario", "1")
            ));
        }
        else
        {
            // Régimen General - IVA included in price (TipoFrase 1)
            frases.Add(new XElement(DteNs + "Frase",
                new XAttribute("TipoFrase", "1"),
                new XAttribute("CodigoEscenario", "1")
            ));
        }

        return frases;
    }

    private XElement CreateItems(EleventaSale sale, string dteType)
    {
        var items = new XElement(DteNs + "Items");
        int lineNumber = 1;

        foreach (var item in sale.Items)
        {
            decimal price;
            decimal taxAmount;

            if (dteType == "FPEQ")
            {
                // Pequeño Contribuyente: no IVA breakdown
                price = item.UnitPrice;
                taxAmount = 0;
            }
            else
            {
                // Régimen General: IVA is 12%, included in price
                price = item.UnitPrice;
                taxAmount = Math.Round(item.LineTotal - (item.LineTotal / 1.12m), 2);
            }

            var xmlItem = new XElement(DteNs + "Item",
                new XAttribute("BienOServicio", "B"),
                new XAttribute("NumeroLinea", lineNumber),
                new XElement(DteNs + "Cantidad", item.Quantity.ToString("F2")),
                new XElement(DteNs + "UnidadMedida", item.UnitOfMeasure),
                new XElement(DteNs + "Descripcion", item.Description),
                new XElement(DteNs + "PrecioUnitario", price.ToString("F2")),
                new XElement(DteNs + "Precio", item.LineTotal.ToString("F2")),
                new XElement(DteNs + "Descuento", item.Discount.ToString("F2"))
            );

            if (dteType != "FPEQ")
            {
                xmlItem.Add(new XElement(DteNs + "Impuestos",
                    new XElement(DteNs + "Impuesto",
                        new XElement(DteNs + "NombreCorto", "IVA"),
                        new XElement(DteNs + "CodigoUnidadGravable", "1"),
                        new XElement(DteNs + "MontoGravable", (item.LineTotal - taxAmount).ToString("F2")),
                        new XElement(DteNs + "MontoImpuesto", taxAmount.ToString("F2"))
                    )
                ));
            }

            xmlItem.Add(new XElement(DteNs + "Total", item.LineTotal.ToString("F2")));
            items.Add(xmlItem);
            lineNumber++;
        }

        return items;
    }

    private XElement CreateTotales(EleventaSale sale, string dteType)
    {
        var totales = new XElement(DteNs + "Totales");

        if (dteType != "FPEQ")
        {
            var totalIva = Math.Round(sale.Total - (sale.Total / 1.12m), 2);

            totales.Add(new XElement(DteNs + "TotalImpuestos",
                new XElement(DteNs + "TotalImpuesto",
                    new XAttribute("NombreCorto", "IVA"),
                    new XAttribute("TotalMontoImpuesto", totalIva.ToString("F2"))
                )
            ));
        }

        totales.Add(new XElement(DteNs + "GranTotal", sale.Total.ToString("F2")));

        return totales;
    }
}
