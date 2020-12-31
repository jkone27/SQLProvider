module Dacpac.ParseTableTests
open NUnit.Framework
open System.Xml
open Utils
open System

let parseXml(xml: string) =
    let removeBrackets (s: string) = s.Replace("[", "").Replace("]", "")
    let doc, node, nodes = xml |> toXmlNamespaceDoc "http://schemas.microsoft.com/sqlserver/dac/Serialization/2012/02"
    let model = doc :> XmlNode |> node "/x:DataSchemaModel/x:Model"

    let parsePrimaryKeyConstraint (pkElement: XmlNode) =
        let name = pkElement |> att "Name"
        let relationship = pkElement |> nodes "x:Relationship" |> Seq.find (fun r -> r |> att "Name" = "ColumnSpecifications")
        let columns =
            relationship
            |> nodes "x:Entry"
            |> Seq.map (node "x:Element/x:Relationship/x:Entry/x:References" >> att "Name")
            |> Seq.map (fun full -> { ConstraintColumn.FullName = full })
            |> Seq.toList
        { PrimaryKeyConstraint.Name = name
          PrimaryKeyConstraint.Columns = columns }

    let pkConstraintsByColumn =
        model
        |> nodes "x:Element"
        |> Seq.filter (fun e -> e |> att "Type" = "SqlPrimaryKeyConstraint")
        |> Seq.map parsePrimaryKeyConstraint
        |> Seq.collect (fun pk -> pk.Columns |> List.map (fun col -> col, pk))
        |> Seq.toList

    let parseFkRelationship (fkElement: XmlNode) =
        let name = fkElement |> att "Name"
        let localColumns = fkElement |> nodes "x:Relationship" |> Seq.find(fun r -> r |> att "Name" = "Columns") |> nodes "x:Entry/x:References" |> Seq.map (att "Name")
        let localTable = fkElement |> nodes "x:Relationship" |> Seq.find(fun r -> r |> att "Name" = "DefiningTable") |> node "x:Entry/x:References" |> att "Name"
        let foreignColumns = fkElement |> nodes "x:Relationship" |> Seq.find(fun r -> r |> att "Name" = "ForeignColumns") |> nodes "x:Entry/x:References" |> Seq.map (att "Name")
        let foreignTable = fkElement |> nodes "x:Relationship" |> Seq.find(fun r -> r |> att "Name" = "ForeignTable") |> node "x:Entry/x:References" |> att "Name"
        { SsdtRelationship.Name = name
          SsdtRelationship.DefiningTable =
            { RefTable.FullName = localTable
              RefTable.Columns = localColumns |> Seq.map (fun fnm -> { ConstraintColumn.FullName = fnm } ) |> Seq.toList }
          SsdtRelationship.ForeignTable =
            { RefTable.FullName = foreignTable
              RefTable.Columns = foreignColumns |> Seq.map (fun fnm -> { ConstraintColumn.FullName = fnm } ) |> Seq.toList } }

    let relationships =
        model
        |> nodes "x:Element"
        |> Seq.filter (fun e -> e |> att "Type" = "SqlForeignKeyConstraint")
        |> Seq.map parseFkRelationship
        |> Seq.toList
            
    let parseColumn (colEntry: XmlNode) =
        let el = colEntry |> node "x:Element"
        let colType, fullName = el |> att "Type", el |> att "Name"
        match colType with
        | "SqlSimpleColumn" -> 
            let allowNulls = el |> nodes "x:Property" |> Seq.tryFind (fun p -> p |> att "Name" = "IsNullable") |> Option.map (fun p -> p |> att "Value")
            let isIdentity = el |> nodes "x:Property" |> Seq.tryFind (fun p -> p |> att "Name" = "IsIdentity") |> Option.map (fun p -> p |> att "Value")
            let dataType = el |> node "x:Relationship/x:Entry/x:Element/x:Relationship/x:Entry/x:References" |> att "Name"
            Some
                { SsdtColumn.Name = fullName.Split([|'.';'[';']'|], StringSplitOptions.RemoveEmptyEntries) |> Array.last
                  SsdtColumn.FullName = fullName
                  SsdtColumn.AllowNulls = match allowNulls with | Some allowNulls -> allowNulls = "True" | _ -> true
                  SsdtColumn.DataType = dataType |> removeBrackets
                  SsdtColumn.HasDefault = false
                  SsdtColumn.IsIdentity = isIdentity |> Option.map (fun isId -> isId = "True") |> Option.defaultValue false }
        | _ ->
            None // Unsupported column type

    let parseTable (tblElement: XmlNode) =
        let fullName = tblElement |> att "Name"
        let relationship = tblElement |> nodes "x:Relationship" |> Seq.find (fun r -> r |> att "Name" = "Columns")
        let columns = relationship |> nodes "x:Entry" |> Seq.choose parseColumn |> Seq.toList
        let nameParts = fullName.Split([|'.'|], StringSplitOptions.RemoveEmptyEntries)
        let primaryKey =
            columns
            |> List.choose(fun c -> pkConstraintsByColumn |> List.tryFind(fun (colRef, pk) -> colRef.FullName = c.FullName))
            |> List.tryHead
            |> Option.map snd
        { SsdtTable.Schema = match nameParts with | [|schema;name|] -> schema |> removeBrackets | _ -> failwithf "Unable to parse table '%s' schema." fullName
          SsdtTable.Name = match nameParts with | [|schema;name|] -> name | [|name|] -> name |> removeBrackets | _ -> failwithf "Unable to parse table '%s' name." fullName
          SsdtTable.FullName = fullName
          SsdtTable.Columns = columns
          SsdtTable.IsView = false
          SsdtTable.PrimaryKey = primaryKey }

    let tables = 
        model
        |> nodes "x:Element"
        |> Seq.filter (fun e -> e |> att "Type" = "SqlTable")
        |> Seq.map parseTable
        |> Seq.toList

    { SsdtSchema.Tables = tables // TODO: @ views
      SsdtSchema.StoredProcs = []
      SsdtSchema.TryGetTableByName = fun nm -> tables |> List.tryFind (fun t -> t.Name = nm)
      SsdtSchema.Relationships = relationships }

open UnzipTests

[<Test>]
let ``Parse Dacpac Model`` () =
    let xml = extractModelXml dacPacPath
    let tables = parseXml xml

    printfn "%A" tables
