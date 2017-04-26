module Client 
private;
{
  ClientMachine
}

module Server 
private;
{
  ServerMachine,
  HelperMachine
}

module ServerAbs 
private;
{
  ServerAbstraction
}

module TestDriver1 
private;
{
  TestDriver_1Client1Server
}

module TestDriver2 
private;
{
  TestDriver_Refinement
}

//test case 0: check that our abstraction is sound.
test testcase0: 
  (compose (rename TestDriver_Refinement to Main in TestDriver2), Server) 
  refines 
  (compose (rename TestDriver_Refinement to Main in TestDriver2), 
           (rename ServerAbstraction to ServerMachine in ServerAbs));

//test case 1: check that the compositin of client and serverAbs is safe (no local assertion failures).
test testcase1: 
  (compose (rename TestDriver_1Client1Server to Main in TestDriver1), 
           Client, 
           ServerAbs);

//test case 2: check that the client satisfies the spec of ReqIdsAreMonotonicallyIncreasing
test testcase2: 
  (compose (rename TestDriver_1Client1Server to Main in TestDriver1), 
           (assert ReqIdsAreMonotonicallyIncreasing in Client), 
           ServerAbs);

//final c code generation for the implementation.
//note that implementation module need not be closed.
implementation (compose Client, Server);