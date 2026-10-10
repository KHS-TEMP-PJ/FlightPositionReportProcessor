import { Stack, StackProps } from 'aws-cdk-lib';
import { Construct } from 'constructs';
interface Props extends StackProps {
    persistent: boolean;
}
export declare class FlightPositionStack extends Stack {
    constructor(scope: Construct, id: string, props: Props);
}
export {};
