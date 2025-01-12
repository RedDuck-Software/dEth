module TestBase

open Nethereum.Web3
open FsUnit.Xunit
open Microsoft.FSharp.Control
open FSharp.Data
open System
open Nethereum.RPC.Eth.DTOs
open System.Numerics
open Nethereum.Hex.HexTypes
open System.IO
open Newtonsoft.Json
open Newtonsoft.Json.Linq
open Nethereum.Contracts
open Nethereum.Hex.HexConvertors.Extensions
open System.Text
open Constants
open Foundry.Contracts.Debug.ContractDefinition
open System.Threading.Tasks
open Nethereum.Web3.Accounts

let rnd = Random()

let rec rndRange min max  = 
    seq { 
        yield rnd.Next(min,max) |> BigInteger
        yield! rndRange min max
        }

let bigInt (value: uint64) = BigInteger(value)
let hexBigInt (value: uint64) = HexBigInteger(bigInt value)

let runNow (task:Task<'T>) =
    task
    |> Async.AwaitTask
    |> Async.RunSynchronously

type Abi(filename) =
    member val JsonString = File.OpenText(filename).ReadToEnd()
    member this.AbiString = JsonConvert.DeserializeObject<JObject>(this.JsonString).GetValue("abi").ToString()
    member this.Bytecode = JsonConvert.DeserializeObject<JObject>(this.JsonString).GetValue("bytecode").ToString()

type IAsyncTxSender =
    abstract member SendTxAsync : string -> BigInteger -> string -> Task<TransactionReceipt>

type EthereumConnection(nodeURI: string, privKey: string) =
    member val public Gas = hexBigInt 4000000UL
    member val public GasPrice = hexBigInt 1000000000UL
    member val public Account = Accounts.Account(privKey)
    member val public Web3 = Web3(Accounts.Account(privKey), nodeURI)

    interface IAsyncTxSender with
        member this.SendTxAsync toAddress value data = 
            let input: TransactionInput =
                TransactionInput(
                    data, 
                    toAddress, 
                    this.Account.Address, 
                    this.Gas, 
                    this.GasPrice, 
                    HexBigInteger(value))
            this.Web3.Eth.TransactionManager.SendTransactionAndWaitForReceiptAsync(input, null)

    member this.DeployContractAsync (abi: Abi) (arguments: obj array) =
        this.Web3.Eth.DeployContract.SendRequestAndWaitForReceiptAsync(
            abi.AbiString, 
            abi.Bytecode, 
            this.Account.Address, 
            this.Gas, this.GasPrice, 
            hexBigInt 0UL, 
            null, 
            arguments)  
                
    member this.TimeTravel seconds = 
        this.Web3.Client.SendRequestAsync(method = "evm_increaseTime", paramList = [| seconds |]) 
        |> Async.AwaitTask 
        |> Async.RunSynchronously
        this.Web3.Client.SendRequestAsync(method = "evm_mine", paramList = [||]) 
        |> Async.AwaitTask 
        |> Async.RunSynchronously

    member this.GetEtherBalance address = 
        let hexBigIntResult = this.Web3.Eth.GetBalance.SendRequestAsync(address) |> runNow
        hexBigIntResult.Value

    member this.SendEtherAsync address (amount:BigInteger) =
        let transactionInput =
            TransactionInput
                ("", address, this.Account.Address, hexBigInt 4000000UL, hexBigInt 1000000000UL, HexBigInteger(amount))
        this.Web3.Eth.TransactionManager.SendTransactionAndWaitForReceiptAsync(transactionInput, null)

    member this.SendEther address amount =
        this.SendEtherAsync address amount |> runNow


type Profile = { FunctionName: string; Duration: string }

let profileMe f =
    let start = DateTime.Now
    let result = f()
    let duration = DateTime.Now - start
    (f.GetType(), duration) |> printf "(Function, Duration) = %A\n"
    result


type ContractPlug(ethConn: EthereumConnection, abi: Abi, address) =
    member val public Address = address

    member val public Contract = 
        ethConn.Web3.Eth.GetContract(abi.AbiString, address)
        
    member this.Function functionName = 
        this.Contract.GetFunction(functionName)

    member this.QueryObjAsync<'a when 'a: (new: unit -> 'a)> functionName arguments = 
        (this.Function functionName).CallDeserializingToObjectAsync<'a> (arguments)

    member this.QueryObj<'a when 'a: (new: unit -> 'a)> functionName arguments = 
        this.QueryObjAsync<'a> functionName arguments |> runNow

    member this.QueryAsync<'a> functionName arguments = 
        (this.Function functionName).CallAsync<'a> (arguments)

    member this.Query<'a> functionName arguments = 
        this.QueryAsync<'a> functionName arguments |> runNow

    member this.FunctionData functionName arguments = 
        (this.Function functionName).GetData(arguments)

    member this.ExecuteFunctionFromAsync functionName arguments (connection:IAsyncTxSender) = 
        this.FunctionData functionName arguments |> connection.SendTxAsync this.Address (BigInteger(0))

    member this.ExecuteFunctionFrom functionName arguments connection = 
        this.ExecuteFunctionFromAsync functionName arguments connection |> runNow

    member this.ExecuteFunctionAsync functionName arguments = 
        this.ExecuteFunctionFromAsync functionName arguments ethConn

    member this.ExecuteFunction functionName arguments = 
        this.ExecuteFunctionAsync functionName arguments |> runNow
            

type Debug(ethConn: EthereumConnection) =
    member val public EthConn = ethConn
    member val public AsyncTxSender = ethConn :> IAsyncTxSender

    member val public  ContractPlug =
        let abi = Abi("../../../../build/contracts/Debug.json")
        let deployTxReceipt = ethConn.DeployContractAsync abi [||] |> runNow
        ContractPlug(ethConn, abi, deployTxReceipt.ContractAddress)

    interface IAsyncTxSender with
        member this.SendTxAsync(toAddress:string) (value:BigInteger) (data: string):Threading.Tasks.Task<TransactionReceipt> = 
            let data =
                this.ContractPlug.FunctionData "forward"
                    [| toAddress
                       data.HexToByteArray() |]
            data |> this.AsyncTxSender.SendTxAsync this.ContractPlug.Address value

    member this.DecodeForwardedEvents(receipt: TransactionReceipt) =
        receipt.DecodeAllEvents<ForwardedEventDTO>() |> Seq.map (fun i -> i.Event)

    member this.BlockTimestamp:BigInteger = 
        this.ContractPlug.Query "blockTimestamp" [||]

type ForwardedEventDTO with
    member this.ResultAsRevertMessage =
        match this.Success with
        | true -> None
        | _ -> Some(Encoding.ASCII.GetString(this.ResultData))

[<System.AttributeUsage(AttributeTargets.Method, AllowMultiple = true)>]
type SpecificationAttribute(contractName, functionName, specCode) =
    inherit Attribute()
    member _.ContractName: string = contractName
    member _.FunctionName: string = functionName
    member _.SpecCode: int = specCode

let useRinkeby = false
let hardhatURI = "http://localhost:8545"
let rinkebyURI = "https://rinkeby.infura.io/v3/c48bc466281c4fefb3decad63c4fc815"
let ganacheMnemonic = "join topple vapor pepper sell enter isolate pact syrup shoulder route token"
let hardhatPrivKey = "ac0974bec39a17e36ba4a6b4d238ff944bacb478cbed5efcae784d7bf4f2ff80"
let rinkebyPrivKey = "5ca35a65adbd49af639a3686d7d438dba1bcef97cf1593cd5dd8fd79ca89fa3c"

let isRinkeby rinkeby notRinkeby =
    match useRinkeby with
    | true -> rinkeby
    | false -> notRinkeby

let ethConn =
    isRinkeby (EthereumConnection(rinkebyURI, rinkebyPrivKey)) (EthereumConnection(hardhatURI, hardhatPrivKey))

let debug = Debug(ethConn)

let shouldEqualIgnoringCase (a: string) (b: string) =
    let aString = a |> string
    let bString = b |> string
    should equal (aString.ToLower()) (bString.ToLower())

let shouldSucceed (txr: TransactionReceipt) = txr.Status |> should equal (hexBigInt 1UL)
let shouldFail (txr: TransactionReceipt) = txr.Status |> should equal (hexBigInt 0UL)

let shouldRevertWithMessage expectedMessage (forwardedEvent: ForwardedEventDTO) =
    match forwardedEvent.ResultAsRevertMessage with
    | None -> failwith "not a revert message"
    | Some actualMessage -> actualMessage |> should haveSubstring expectedMessage

let shouldRevertWithUnknownMessage (forwardedEvent: ForwardedEventDTO) =
    shouldRevertWithMessage "" forwardedEvent

let decodeEvents<'a when 'a: (new: unit -> 'a)> (receipt: TransactionReceipt) =
    receipt.DecodeAllEvents<'a>() |> Seq.map (fun e -> e.Event)

let decodeFirstEvent<'a when 'a: (new: unit -> 'a)> (receipt: TransactionReceipt) =
    decodeEvents<'a> receipt |> Seq.head

let makeAccount() =
    let ecKey = Nethereum.Signer.EthECKey.GenerateKey();
    let privateKey = ecKey.GetPrivateKeyAsBytes().ToHex();
    Account(privateKey);


let startOfSale = debug.BlockTimestamp + BigInteger (1UL * hours)
let bucketPeriod = 7UL * hours |> BigInteger
let bucketSupply = 50000UL |> BigInteger
let bucketCount = 1250UL |> BigInteger
